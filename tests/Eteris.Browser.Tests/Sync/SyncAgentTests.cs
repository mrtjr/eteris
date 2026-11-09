using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Google;
using Eteris.Sync.Model;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Perfil local em memória (o que o navegador tem: favoritos, definições, Escudo, Espaços, notas).
internal sealed class MemoryProfile : ILocalProfile
{
    private readonly Lock _gate = new();
    public readonly Dictionary<string, JsonObject> Items = new(StringComparer.Ordinal);

    // Alterações que este «aparelho» recusa aplicar (por exemplo apagar um Espaço com abas).
    public Func<LocalChange, bool> Refuse { get; set; } = _ => false;

    // Corre a meio de uma ronda (depois de ler o perfil, antes de aplicar): o utilizador a mexer durante a sincronização.
    public Action? DuringCycle { get; set; }

    public int Collects { get; private set; }

    public void Set(string collection, string key, JsonObject data)
    {
        lock (_gate) Items[ProfileSync.Id(collection, key)] = data;
    }

    public void Remove(string collection, string key)
    {
        lock (_gate) Items.Remove(ProfileSync.Id(collection, key));
    }

    public JsonObject? Get(string collection, string key)
    {
        lock (_gate) return Items.GetValueOrDefault(ProfileSync.Id(collection, key));
    }

    public string? Title(string url) => Get(Collections.Bookmarks, url)?["title"]?.GetValue<string>();

    public string Notes => Get(Collections.Notes, Collections.NotesKey)?["text"]?.GetValue<string>() ?? string.Empty;

    public void Bookmark(string url, string title) => Set(Collections.Bookmarks, url, new JsonObject { ["title"] = title });

    public void Setting(string key, string value) => Set(Collections.Settings, key, new JsonObject { ["value"] = value });

    public void SetNotes(string text)
    {
        if (text.Length == 0) Remove(Collections.Notes, Collections.NotesKey);
        else Set(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = text });
    }

    public IReadOnlyList<LocalItem> Collect()
    {
        Action? during;
        List<LocalItem> items;
        lock (_gate)
        {
            Collects++;
            items = Items.Select(i =>
            {
                var at = i.Key.IndexOf('\n');
                return new LocalItem(i.Key[..at], i.Key[(at + 1)..], i.Value.DeepClone().AsObject());
            }).ToList();
            // Lido o perfil, corre DuringCycle (uma vez): a alteração chega depois desta leitura e antes da seguinte.
            during = DuringCycle;
            DuringCycle = null;
        }
        during?.Invoke();
        return items;
    }

    public IReadOnlyCollection<LocalChange> Apply(IReadOnlyList<LocalChange> changes)
    {
        var applied = new List<LocalChange>();
        lock (_gate)
        {
            foreach (var change in changes)
            {
                if (Refuse(change)) continue;
                var id = ProfileSync.Id(change.Collection, change.Key);
                if (change.Data is null) Items.Remove(id);
                else Items[id] = change.Data.DeepClone().AsObject();
                applied.Add(change);
            }
        }
        return applied;
    }

    public string Fingerprint()
    {
        lock (_gate)
            return string.Join("|", Items.Where(i => !i.Key.StartsWith(Collections.Notes + "\n", StringComparison.Ordinal))
                .OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Key + "=" + ProfileSync.Canonical(i.Key[..i.Key.IndexOf('\n')], i.Value)))
                + "|notes=" + Notes;
    }
}

// Estado guardado como o navegador o guarda (JSON, ida e volta a cada gravação: prova que nada se perde ao reabrir).
internal sealed class MemoryStore : ISyncStore
{
    private byte[]? _bytes;

    public int Saves { get; private set; }

    public SavedSync? Load() => _bytes is null ? null : Json.Parse<SavedSync>(_bytes);

    public void Save(SavedSync saved)
    {
        _bytes = Json.Bytes(saved);
        Saves++;
    }

    public void Delete() => _bytes = null;
}

public sealed class SyncAgentTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-sync-agent-" + Guid.NewGuid().ToString("N"));

    private long Now() => _now;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store);

    private Device NewDevice(string name, Func<ISyncTransport>? transport = null, MemoryStore? store = null, MemoryProfile? profile = null)
    {
        profile ??= new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport ?? (() => new FolderTransport(_folder)), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) };
        return new Device(agent, profile, store);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "À espera de: " + what);
    }

    private static async Task Settle(params Device[] devices)
    {
        for (var round = 0; round < 3; round++)
            foreach (var device in devices)
                Assert.True(await device.Agent.RunAsync());
    }

    // ---- criar e entrar ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstDevice_CreatesTheAccount_ShowsTheCodeOnce_AndUploadsEverything()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        casa.Profile.Setting("theme", "light");
        casa.Profile.SetNotes("comprar pão");
        Assert.Equal(SyncPhase.Off, casa.Agent.Status.Phase);

        await casa.Agent.StartAsync();
        var status = casa.Agent.Status;
        Assert.Equal(SyncPhase.On, status.Phase);
        Assert.Null(status.Error);
        Assert.NotNull(status.RecoveryCode);
        Assert.Equal(14, status.RecoveryCode!.Split(' ', '-').Length);
        Assert.True(status.LastSuccessMs > 0);

        // O código continua à vista até «Já guardei» — também depois de reabrir o Éteris — e depois nunca mais.
        var reopened = NewDevice("Casa", store: casa.Store, profile: casa.Profile);
        Assert.Equal(status.RecoveryCode, reopened.Agent.Status.RecoveryCode);
        reopened.Agent.AcknowledgeCode();
        Assert.Null(reopened.Agent.Status.RecoveryCode);
        Assert.Null(NewDevice("Casa", store: casa.Store, profile: casa.Profile).Agent.Status.RecoveryCode);

        // Nenhum texto do perfil em claro na pasta (só ficheiros cifrados).
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("wikipedia", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("comprar", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Casa", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SecondDevice_SeesTheAccount_JoinsWithTheRecoveryCode_AndNothingIsLost()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        casa.Profile.Bookmark("https://comum.example/", "Título da conta");
        casa.Profile.Setting("theme", "light");
        casa.Profile.SetNotes("da casa");
        await casa.Agent.StartAsync();
        var code = casa.Agent.Status.RecoveryCode!;

        var trabalho = NewDevice("Trabalho");
        trabalho.Profile.Bookmark("https://so-no-trabalho.example/", "Só aqui");
        trabalho.Profile.Bookmark("https://comum.example/", "Título local");
        trabalho.Profile.Setting("theme", "dark");
        trabalho.Profile.SetNotes("do trabalho");
        await trabalho.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, trabalho.Agent.Status.Phase);
        Assert.Null(trabalho.Agent.Status.RecoveryCode);

        await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.JoinWithCodeAsync("0000-0000"));
        await trabalho.Agent.JoinWithCodeAsync(code.ToLowerInvariant());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        await Settle(casa, trabalho);

        foreach (var device in new[] { casa, trabalho })
        {
            Assert.Equal("Wikipédia", device.Profile.Title("https://pt.wikipedia.org/"));
            Assert.Equal("Só aqui", device.Profile.Title("https://so-no-trabalho.example/")); // o que só existia aqui sobe
            Assert.Equal("Título da conta", device.Profile.Title("https://comum.example/"));  // o da conta vale
            Assert.Equal("light", device.Profile.Get(Collections.Settings, "theme")!["value"]!.GetValue<string>());
            Assert.Equal("da casa\n\n— Notas de «Trabalho» —\ndo trabalho", device.Profile.Notes);
        }
        Assert.Equal(casa.Profile.Fingerprint(), trabalho.Profile.Fingerprint());
    }

    [Fact]
    public async Task ChangesBothWays_Conflicts_Tombstones_AndEditsDuringARound()
    {
        var (casa, trabalho) = await TwoAsync();

        // Nos dois sentidos.
        casa.Profile.Bookmark("https://a.example/", "A");
        trabalho.Profile.Setting("density", "compact");
        trabalho.Profile.Set(Collections.Shield, "strict|news.example|", new JsonObject());
        await Settle(casa, trabalho);
        Assert.Equal("A", trabalho.Profile.Title("https://a.example/"));
        Assert.NotNull(casa.Profile.Get(Collections.Settings, "density"));
        Assert.NotNull(casa.Profile.Get(Collections.Shield, "strict|news.example|"));

        // Conflito: o mesmo favorito mudado nos dois; ganha a alteração mais recente (HLC) e os dois ficam iguais.
        // (Cada aparelho sincroniza 10 s depois da sua alteração: a versão é a hora dessa ronda.)
        casa.Profile.Bookmark("https://a.example/", "A (casa)");
        Assert.True(await casa.Agent.RunAsync());
        _now += 1000;
        trabalho.Profile.Bookmark("https://a.example/", "A (trabalho)"); // o trabalho ainda não recebeu a da casa
        await Settle(trabalho, casa);
        Assert.Equal("A (trabalho)", casa.Profile.Title("https://a.example/"));
        Assert.Equal("A (trabalho)", trabalho.Profile.Title("https://a.example/"));

        // Notas mudadas nos dois em linhas diferentes: fundem-se.
        casa.Profile.SetNotes("linha 1\nlinha 2\nlinha 3");
        await Settle(casa, trabalho);
        casa.Profile.SetNotes("linha 1 (casa)\nlinha 2\nlinha 3");
        trabalho.Profile.SetNotes("linha 1\nlinha 2\nlinha 3 (trabalho)");
        await Settle(casa, trabalho);
        Assert.Equal("linha 1 (casa)\nlinha 2\nlinha 3 (trabalho)", casa.Profile.Notes);
        Assert.Equal(casa.Profile.Notes, trabalho.Profile.Notes);

        // Lápide: apagado num, sai no outro e não ressuscita.
        trabalho.Profile.Remove(Collections.Bookmarks, "https://a.example/");
        await Settle(casa, trabalho);
        Assert.Null(casa.Profile.Title("https://a.example/"));
        await Settle(casa, trabalho);
        Assert.Null(trabalho.Profile.Title("https://a.example/"));

        // O utilizador muda um favorito a meio de uma ronda que traz outra versão dele: a dele fica e segue depois.
        casa.Profile.Bookmark("https://b.example/", "B");
        await Settle(casa, trabalho);
        trabalho.Profile.Bookmark("https://b.example/", "B (remoto)");
        Assert.True(await trabalho.Agent.RunAsync());
        casa.Profile.DuringCycle = () => casa.Profile.Bookmark("https://b.example/", "B (escrito agora)");
        _now += 1000;
        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal("B (escrito agora)", casa.Profile.Title("https://b.example/"));
        _now += 1000;
        await Settle(casa, trabalho);
        Assert.Equal("B (escrito agora)", trabalho.Profile.Title("https://b.example/"));
        Assert.Equal(casa.Profile.Fingerprint(), trabalho.Profile.Fingerprint());
    }

    [Fact]
    public async Task RefusedChange_StaysLocal_AndDoesNotResurrectForTheOthers()
    {
        var (casa, trabalho) = await TwoAsync();
        var space = new JsonObject { ["name"] = "Estudo", ["color"] = 2, ["order"] = 1 };
        casa.Profile.Set(Collections.Spaces, "Estudo", space);
        await Settle(casa, trabalho);
        Assert.NotNull(trabalho.Profile.Get(Collections.Spaces, "Estudo"));

        // O Espaço tem abas no trabalho: apagá-lo lá não se aplica (as abas são desse computador).
        trabalho.Profile.Refuse = c => c.Collection == Collections.Spaces && c.Remove;
        casa.Profile.Remove(Collections.Spaces, "Estudo");
        await Settle(casa, trabalho);
        Assert.NotNull(trabalho.Profile.Get(Collections.Spaces, "Estudo"));
        Assert.Null(casa.Profile.Get(Collections.Spaces, "Estudo")); // nem volta para a casa
    }

    [Fact]
    public async Task UnknownFields_FromANewerEteris_AreKept()
    {
        var (casa, trabalho) = await TwoAsync();
        // Um Éteris mais recente, na mesma conta, escreve um campo que estes não conhecem («folder»).
        using (var secret = Eteris.Sync.Crypto.AccountSecret.FromCode(casa.Agent.Status.RecoveryCode!))
        {
            var newer = await SyncEngine.JoinAsync(new FolderTransport(_folder), secret, DeviceIdentity.Create("Mais recente"), Now);
            newer.Put(Collections.Bookmarks, "https://c.example/", new JsonObject { ["title"] = "C", ["folder"] = "Receitas" });
            await newer.SyncAsync();
        }
        await Settle(casa, trabalho);
        Assert.Equal("C", trabalho.Profile.Title("https://c.example/"));
        Assert.Null(trabalho.Profile.Get(Collections.Bookmarks, "https://c.example/")!["folder"]);
        // O «trabalho» só conhece o título; muda-o, e a pasta que não conhece continua no registo.
        _now += 1000;
        trabalho.Profile.Bookmark("https://c.example/", "C2");
        await Settle(casa, trabalho);
        Assert.Equal("C2", casa.Profile.Title("https://c.example/"));
        var saved = casa.Store.Load()!.Engine!.Records.Values.Single(r => r.Key == "https://c.example/");
        Assert.Equal("Receitas", saved.Data!["folder"]!.GetValue<string>());
    }

    // ---- emparelhamento pedido pelo aparelho novo -------------------------------------------------------------------

    [Fact]
    public async Task NewDevice_AsksForApproval_BothShowTheSameCode_AndJoinsAfterBothConfirm()
    {
        var google = new FakeGoogle();
        var (casa, _) = (NewDevice("Casa", DriveFor(google)), 0);
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        await casa.Agent.StartAsync();
        casa.Agent.AcknowledgeCode();

        var portatil = NewDevice("Portátil", DriveFor(google));
        portatil.Profile.Bookmark("https://portatil.example/", "Do portátil");
        await portatil.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, portatil.Agent.Status.Phase);

        portatil.Agent.RequestPairing();
        Assert.Equal(SyncPhase.Pairing, portatil.Agent.Status.Phase);
        // No outro computador: o utilizador abre o menu da conta ou as Definições → procura pedidos.
        await Until(() => google.Files.Any(f => f.Name.EndsWith("/0-request", StringComparison.Ordinal)), "o pedido no Drive");
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1 && portatil.Agent.Status.Pairing?.Code is not null, "o código nos dois");
        var request = casa.Agent.Status.Requests[0];
        Assert.Equal("Portátil", request.Name);
        Assert.Equal(portatil.Agent.Status.Pairing!.Code, request.Code);
        Assert.Matches(@"^\d{3} \d{3}$", request.Code);

        // Nenhum nome nem segredo em claro nos ficheiros do pedido.
        foreach (var (name, data) in google.Files.Where(f => f.Name.Contains("/pair/", StringComparison.Ordinal)))
            Assert.DoesNotContain("Portátil", System.Text.Encoding.UTF8.GetString(data), StringComparison.Ordinal);

        portatil.Agent.ConfirmPairing();
        await casa.Agent.ApproveAsync(request.Session);
        await Until(() => portatil.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
        Assert.Contains("Casa", portatil.Agent.Status.Notice);
        Assert.Null(portatil.Agent.Status.RecoveryCode); // o código só aparece no aparelho que cria a conta
        await Settle(casa, portatil);
        Assert.Equal("Do portátil", casa.Profile.Title("https://portatil.example/"));
        Assert.Equal("Wikipédia", portatil.Profile.Title("https://pt.wikipedia.org/"));
        // Os ficheiros do emparelhamento saem no fim.
        Assert.DoesNotContain(google.Files, f => f.Name.Contains("/pair/", StringComparison.Ordinal) && !f.Name.EndsWith("/x-host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pairing_RefusedOnTheOtherComputer_GoesBackToChoose()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var novo = NewDevice("Novo");
        await novo.Agent.StartAsync();
        novo.Agent.RequestPairing();
        await Until(() => Directory.Exists(Path.Combine(_folder, "eteris", "pair")), "o pedido");
        await Until(() => { casa.Agent.CheckRequestsAsync().Wait(); return casa.Agent.Status.Requests.Count == 1; }, "o pedido no anfitrião");
        await casa.Agent.RefuseAsync(casa.Agent.Status.Requests[0].Session);
        await Until(() => novo.Agent.Status.Pairing?.Error is not null, "a recusa");
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.Contains("recusou", novo.Agent.Status.Pairing!.Error);
        Assert.Empty(casa.Agent.Status.Requests);
    }

    // ---- aparelhos ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveDevice_NewKeysForTheOthers_TheRemovedOneStops()
    {
        var (casa, trabalho) = await TwoAsync();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho, portatil);
        var devices = await casa.Agent.RefreshDevicesAsync();
        Assert.Equal(3, devices.Count);
        Assert.True(devices[0].IsThis);
        var oldCode = casa.Store.Load()!.Secret;

        trabalho.Profile.Bookmark("https://antes.example/", "Mudado antes de saber"); // ainda por enviar
        var removed = devices.Single(d => d.Name == "Portátil");
        await casa.Agent.RemoveDeviceAsync(removed.Id);
        Assert.NotNull(casa.Agent.Status.RecoveryCode);                // código novo, mostrado uma vez
        Assert.NotEqual(oldCode, casa.Store.Load()!.Secret);
        Assert.Contains("Portátil", casa.Agent.Status.Notice);

        // O que fica recebe a conta nova sozinho, e o que tinha por enviar segue.
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Contains("mudou de chave", trabalho.Agent.Status.Notice);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        casa.Profile.Bookmark("https://depois.example/", "Depois");
        await Settle(casa, trabalho);
        Assert.Equal("Depois", trabalho.Profile.Title("https://depois.example/"));
        Assert.Equal("Mudado antes de saber", casa.Profile.Title("https://antes.example/"));

        // O removido para, sem a conta nova.
        Assert.True(await portatil.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, portatil.Agent.Status.Phase);
        Assert.False(portatil.Agent.Enabled);
        Assert.Null(portatil.Store.Load()!.Secret);
        Assert.Equal(2, (await casa.Agent.RefreshDevicesAsync()).Count(d => d.LastSyncMs > 0));
    }

    [Fact]
    public async Task DeleteCloudData_RemovesEveryFile_AndStopsEverywhere_WithoutLosingLocalData()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Profile.Bookmark("https://fica.example/", "Fica");
        await Settle(casa, trabalho);
        await casa.Agent.DeleteCloudDataAsync();
        Assert.Empty(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories));
        Assert.Equal(SyncPhase.Stopped, casa.Agent.Status.Phase);
        Assert.Contains("apagados", casa.Agent.Status.Notice);
        Assert.Equal("Fica", casa.Profile.Title("https://fica.example/"));

        // O outro aparelho para (não volta a criar a conta sozinho: isso desfazia o «apagar»).
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.Empty(Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories));
        Assert.Equal("Fica", trabalho.Profile.Title("https://fica.example/"));

        // «Ligar outra vez» cria uma conta nova.
        await casa.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
        Assert.NotNull(casa.Agent.Status.RecoveryCode);
    }

    [Fact]
    public async Task SignOut_KeepingTheKey_ResumesWithoutCode_ForgettingIt_AsksAgain()
    {
        var (casa, trabalho) = await TwoAsync();
        trabalho.Agent.SignOut(forgetKey: false);
        Assert.Equal(SyncPhase.Off, trabalho.Agent.Status.Phase);
        Assert.False(trabalho.Agent.Enabled);
        Assert.True(trabalho.Agent.HasKey);
        Assert.True(await trabalho.Agent.RunAsync()); // desligada: nada
        await trabalho.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);

        trabalho.Agent.SignOut(forgetKey: true);
        Assert.False(trabalho.Agent.HasKey);
        Assert.Null(trabalho.Store.Load());
        await trabalho.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, trabalho.Agent.Status.Phase);
    }

    // ---- quando há rede ----------------------------------------------------------------------------------------------

    // Sem a sincronização ligada (nunca se entrou, ou «Sair»): nem o arranque, nem uma ronda do agendador, nem o menu
    // (procurar pedidos, ler aparelhos) fazem pedido nenhum.
    [Fact]
    public async Task WithoutSync_NothingIsEverSent()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        Assert.False(casa.Agent.Enabled);
        Assert.Equal(SyncPhase.Off, casa.Agent.Status.Phase);
        Assert.False(casa.Agent.HasLocalChanges());
        Assert.True(await casa.Agent.RunAsync());
        await casa.Agent.CheckRequestsAsync();
        await casa.Agent.RefreshDevicesAsync();
        Assert.Empty(google.Requests);

        await casa.Agent.StartAsync();
        casa.Agent.SignOut(forgetKey: false);
        var after = google.Requests.Count;
        Assert.True(await casa.Agent.RunAsync());
        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(after, google.Requests.Count);
    }

    // Montar o agente com sessão e ler o estado (o arranque, o menu, as Definições) não faz pedido nenhum à Google;
    // só RunAsync / StartAsync falam com o Drive.
    [Fact]
    public async Task Startup_BuildingTheAgentAndReadingTheState_SendsNothing()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        casa.Profile.Bookmark("https://x.example/", "X");
        var before = google.Requests.Count;

        var reopened = NewDevice("Casa", DriveFor(google), casa.Store, casa.Profile);
        Assert.True(reopened.Agent.Enabled);
        Assert.Equal(SyncPhase.On, reopened.Agent.Status.Phase);
        Assert.True(reopened.Agent.HasLocalChanges());
        Assert.Equal(before, google.Requests.Count);

        Assert.True(await reopened.Agent.RunAsync());
        Assert.True(google.Requests.Count > before);
        Assert.False(reopened.Agent.HasLocalChanges());
        Assert.All(google.Requests, r => Assert.Matches(@"^(GET|POST|PATCH|DELETE) https://(www\.googleapis\.com/(drive/v3/files|upload/drive/v3/files)|oauth2\.googleapis\.com/token)", r));
    }

    [Fact]
    public async Task NoNetwork_FailsQuietly_KeepsTheOutbox_AndRecovers()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        casa.Profile.Bookmark("https://offline.example/", "Offline");
        google.FailNext(System.Net.HttpStatusCode.ServiceUnavailable, 40);
        Assert.False(await casa.Agent.RunAsync());
        Assert.Contains("O Éteris tenta outra vez", casa.Agent.Status.Error);
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
        for (var i = 0; i < 40 && !await casa.Agent.RunAsync(); i++) { }
        Assert.Null(casa.Agent.Status.Error);

        var outro = NewDevice("Outro", DriveFor(google));
        await outro.Agent.StartAsync();
        await outro.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        Assert.Equal("Offline", outro.Profile.Title("https://offline.example/"));
    }

    [Fact]
    public void Schedule_StartupDelay_ChangeWait_Period_AndBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SyncSchedule.StartupDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), SyncSchedule.ChangeDelay);
        Assert.Equal(TimeSpan.FromMinutes(10), SyncSchedule.Period);
        Assert.Equal(TimeSpan.FromSeconds(30), SyncSchedule.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(1), SyncSchedule.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(8), SyncSchedule.Backoff(5));
        Assert.Equal(TimeSpan.FromMinutes(30), SyncSchedule.Backoff(9));
        Assert.Equal(TimeSpan.FromMinutes(30), SyncSchedule.Backoff(100));

        var s = new SyncSchedule();
        Assert.Null(s.NextMs);                                  // sem sincronização ligada: nada marcado
        s.Startup(1_000);
        Assert.Equal(31_000, s.NextMs);                         // 30 s depois de a janela estar pronta
        Assert.Equal(SyncSchedule.Action.None, s.Due(30_999));
        Assert.Equal(SyncSchedule.Action.Full, s.Due(31_000));
        s.Done(31_000, ok: true);
        Assert.Equal(631_000, s.NextMs);                        // de 10 em 10 minutos
        s.LocalChange(40_000);
        Assert.Equal(50_000, s.NextMs);                         // 10 s depois da última alteração
        s.LocalChange(45_000);
        Assert.Equal(55_000, s.NextMs);
        Assert.Equal(SyncSchedule.Action.LocalCheck, s.Due(55_000));
        s.Done(55_000, ok: false);
        Assert.Equal(85_000, s.NextMs);                         // falhou: 30 s
        s.LocalChange(60_000);
        Assert.Equal(85_000, s.NextMs);                         // a espera não encurta com alterações
        s.Done(85_000, ok: false);
        Assert.Equal(145_000, s.NextMs);                        // depois 1 min
        s.Now(100_000);
        Assert.Equal(SyncSchedule.Action.Full, s.Due(100_000)); // «Sincronizar agora» passa à frente
        s.Done(100_000, ok: true);
        Assert.Equal(700_000, s.NextMs);
        s.Stop();
        Assert.Null(s.NextMs);
        Assert.Equal(SyncSchedule.Action.None, s.Due(10_000_000));
    }

    private Func<ISyncTransport> DriveFor(FakeGoogle google)
    {
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var session = new GoogleSession(oauth, new TokenStore(Path.Combine(_folder, "tokens-" + Guid.NewGuid().ToString("N")), new PlainProtector()), Now);
        session.Use(new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope));
        return () => new GoogleDriveTransport(http, session.AccessTokenAsync, (_, _) => Task.CompletedTask);
    }

    private async Task<(Device Casa, Device Trabalho)> TwoAsync()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho);
        return (casa, trabalho);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }
}
