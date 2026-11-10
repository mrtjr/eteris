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
internal sealed class MemoryProfile : ILocalProfileReader
{
    private readonly Lock _gate = new();
    public readonly Dictionary<string, JsonObject> Items = new(StringComparer.Ordinal);

    // Coleções que «não se conseguem ler» agora (ficheiro bloqueado, base de dados ocupada).
    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    public LocalRead Read()
    {
        var items = Collect();
        string[] unreadable;
        lock (_gate) unreadable = [.. Unreadable];
        return new LocalRead(items.Where(i => !unreadable.Contains(i.Collection)).ToList(), unreadable);
    }

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
    public async Task NewDevice_AsksForApproval_TheOldOneTypesTheCode_AndJoinsAfterBothConfirm()
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
        // O computador antigo não mostra o código: pede-o (0.2.15, T8). Só o novo o mostra.
        Assert.Matches(@"^\d{3} \d{3}$", portatil.Agent.Status.Pairing!.Code!);

        // Nenhum nome nem segredo em claro nos ficheiros do pedido.
        foreach (var (name, data) in google.Files.Where(f => f.Name.Contains("/pair/", StringComparison.Ordinal)))
            Assert.DoesNotContain("Portátil", System.Text.Encoding.UTF8.GetString(data), StringComparison.Ordinal);

        portatil.Agent.ConfirmPairing();
        Assert.Equal(Eteris.Sync.Pairing.PairingAnswer.Approved,
            await casa.Agent.ApproveWithCodeAsync(request.Session, portatil.Agent.Status.Pairing!.Code!));
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
        // A chave antiga fica (não serve para a conta nova); o aviso não diz que os dados foram apagados.
        Assert.NotNull(portatil.Store.Load()!.Secret);
        Assert.Null(portatil.Store.Load()!.Engine);
        Assert.Contains("removido noutro aparelho", portatil.Agent.Status.Notice);
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

    // «Apagar os meus dados…» e a janela fecha a meio (ainda «A sincronizar…»): o «desligada» ficou gravado antes do
    // primeiro pedido; ao reabrir continua desligada, nenhuma ronda (agendada ou forçada) envia nada, e o apagar acaba.
    [Fact]
    public async Task DeleteCloudData_ClosedMidway_LeavesTheProfileOff_AndOnReopenFinishesWithoutUploading()
    {
        var (casa, trabalho, reaberta) = await InterruptedDeleteAsync();
        Assert.False(reaberta.Agent.Enabled);
        Assert.Equal(SyncPhase.Stopped, reaberta.Agent.Status.Phase);
        Assert.True(reaberta.Agent.Status.Deleting);
        Assert.True(reaberta.Agent.DeletePending);

        // Uma ronda do agendador (ou forçada) não volta a criar a conta nem envia nada.
        var antes = FilesInFolder();
        Assert.NotEmpty(antes);
        casa.Profile.Bookmark("https://depois.example/", "Depois");
        Assert.True(await reaberta.Agent.RunAsync(force: true));
        Assert.Equal(antes, FilesInFolder());
        Assert.False(reaberta.Agent.HasLocalChanges());

        // Acaba de apagar (o anfitrião retoma-o ao abrir).
        await reaberta.Agent.FinishCloudDeletionAsync();
        Assert.Empty(FilesInFolder());
        Assert.False(reaberta.Agent.Status.Deleting);
        Assert.False(casa.Store.Load()!.DeletePending);
        Assert.False(casa.Store.Load()!.Enabled);
        Assert.Contains("apagados", reaberta.Agent.Status.Notice);
        Assert.True(await reaberta.Agent.RunAsync(force: true));
        Assert.Empty(FilesInFolder());
        Assert.Equal("Fica", casa.Profile.Title("https://fica.example/"));

        // O outro aparelho para, sem voltar a criar a conta.
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.Empty(FilesInFolder());
    }

    // «Ligar» com um apagar por acabar: acaba primeiro de apagar e só depois cria uma conta nova (a antiga não volta).
    [Fact]
    public async Task Start_AfterAnInterruptedDelete_FinishesTheDeleteFirst_ThenCreatesAFreshAccount()
    {
        var (casa, _, reaberta) = await InterruptedDeleteAsync();
        var velhas = FilesInFolder().Select(f => f.Split('/')[1]).Distinct().ToList();
        await reaberta.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, reaberta.Agent.Status.Phase);
        Assert.NotNull(reaberta.Agent.Status.RecoveryCode);
        Assert.False(reaberta.Agent.Status.Deleting);
        var cadeias = FilesInFolder().Select(f => f.Split('/')[1]).Where(c => c != "pair").Distinct().ToList();
        Assert.Single(cadeias);
        Assert.DoesNotContain(cadeias[0], velhas);
        Assert.Equal("Fica", casa.Profile.Title("https://fica.example/"));
    }

    // O apagar chega a meio de uma ronda: o «desligada» fica gravado logo (sem esperar pela ronda), a ronda não grava
    // nem envia depois, e o apagar corre a seguir e deixa a pasta vazia.
    [Fact]
    public async Task DeleteCloudData_DuringARound_IsSavedAtOnce_AndTheRoundNeitherSavesNorUploadsAfterwards()
    {
        var (casa, _) = await TwoAsync();
        Task? apagar = null;
        SavedSync? gravadoLogo = null;
        casa.Profile.Bookmark("https://durante.example/", "Durante");
        casa.Profile.DuringCycle = () =>
        {
            apagar = casa.Agent.DeleteCloudDataAsync();
            gravadoLogo = casa.Store.Load();
        };
        Assert.True(await casa.Agent.RunAsync(force: true));
        Assert.NotNull(apagar);
        await apagar!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(gravadoLogo);
        Assert.False(gravadoLogo!.Enabled);
        Assert.True(gravadoLogo.DeletePending);
        Assert.Null(gravadoLogo.Secret);
        Assert.Empty(FilesInFolder());
        Assert.False(casa.Agent.Enabled);
        Assert.False(casa.Store.Load()!.DeletePending);

        casa.Profile.Bookmark("https://depois.example/", "Depois");
        Assert.True(await casa.Agent.RunAsync(force: true));
        Assert.Empty(FilesInFolder());
    }

    // O mesmo no Google Drive (falso): o pedido de apagar de um ficheiro fica preso, a janela fecha; ao reabrir está
    // desligada, a ronda não faz pedido nenhum, e acabar de apagar não envia nada (só listas e DELETE).
    [Fact]
    public async Task DeleteCloudData_OnTheDrive_ClosedMidway_OnReopenFinishes_AndNothingIsUploadedAgain()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        casa.Profile.Bookmark("https://fica.example/", "Fica");
        await casa.Agent.StartAsync();
        Assert.True(google.FileIds.Count >= 2);
        var (chegou, soltar) = google.Hold("/drive/v3/files/" + google.FileIds[1]);
        try
        {
            using var fechar = new CancellationTokenSource();
            var apagar = casa.Agent.DeleteCloudDataAsync(fechar.Token);
            var gravado = casa.Store.Load()!;
            Assert.False(gravado.Enabled);
            Assert.True(gravado.DeletePending);
            await chegou.WaitAsync(TimeSpan.FromSeconds(30));
            fechar.Cancel(); // a janela fecha a meio
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apagar);
            Assert.NotEmpty(google.Files);

            var reaberta = NewDevice("Casa", DriveFor(google), casa.Store, casa.Profile);
            Assert.False(reaberta.Agent.Enabled);
            Assert.True(reaberta.Agent.Status.Deleting);
            int antes;
            lock (google.Requests) antes = google.Requests.Count;
            Assert.True(await reaberta.Agent.RunAsync(force: true));
            lock (google.Requests) Assert.Equal(antes, google.Requests.Count);
            await reaberta.Agent.FinishCloudDeletionAsync();
            Assert.Empty(google.Files);
            List<string> depois;
            lock (google.Requests) depois = google.Requests.Skip(antes).ToList();
            Assert.DoesNotContain(depois, r => r.StartsWith("POST", StringComparison.Ordinal) || r.StartsWith("PATCH", StringComparison.Ordinal));
            Assert.False(reaberta.Agent.Enabled);
            Assert.Contains("apagados", reaberta.Agent.Status.Notice);
        }
        finally { soltar(); }
    }

    // Dois aparelhos na conta; a «Casa» começa a apagar e a janela fecha depois de apagar alguns ficheiros (o pedido
    // seguinte fica preso até ao fecho). Devolve a «Casa» reaberta (o mesmo estado guardado e o mesmo perfil).
    private async Task<(Device Casa, Device Trabalho, Device Reaberta)> InterruptedDeleteAsync()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Profile.Bookmark("https://fica.example/", "Fica");
        await Settle(casa, trabalho);
        var preso = new StuckTransport(new FolderTransport(_folder), deletesBeforeStuck: 2);
        var aberta = NewDevice("Casa", () => preso, casa.Store, casa.Profile);
        using var fechar = new CancellationTokenSource();
        var apagar = aberta.Agent.DeleteCloudDataAsync(fechar.Token);
        // Gravado antes do primeiro pedido.
        var gravado = casa.Store.Load()!;
        Assert.False(gravado.Enabled);
        Assert.True(gravado.DeletePending);
        Assert.Null(gravado.Secret);
        Assert.Null(gravado.Engine);
        await preso.Stuck.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(aberta.Agent.Status.Deleting);
        fechar.Cancel(); // a janela fecha a meio
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apagar);
        return (casa, trabalho, NewDevice("Casa", store: casa.Store, profile: casa.Profile));
    }

    private List<string> FilesInFolder()
        => Directory.Exists(_folder)
            ? Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_folder, f).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal).ToList()
            : [];

    // Apaga os primeiros ficheiros e prende o pedido seguinte até ao fecho (o token de cancelamento).
    private sealed class StuckTransport(ISyncTransport inner, int deletesBeforeStuck) : ISyncTransport
    {
        private readonly TaskCompletionSource _stuck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deletes;

        public Task Stuck => _stuck.Task;

        public string Name => inner.Name;

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);

        public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _deletes) > deletesBeforeStuck)
            {
                _stuck.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await inner.DeleteAsync(path, cancellationToken);
        }
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

    // ---- revisão do login completo -----------------------------------------------------------------------------------

    // O «trabalho» acrescenta X, muda o título de Y e envia; a «casa» remove o portátil sem ter recebido essa ronda.
    // X e o título novo de Y ficam em todos os aparelhos que continuam.
    [Fact]
    public async Task RemoveDevice_WhatAnotherDeviceSentButThisOneNeverRead_IsKept()
    {
        var (casa, trabalho) = await TwoAsync();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        casa.Profile.Bookmark("https://y.example/", "Y");
        await Settle(casa, trabalho, portatil);
        var devices = await casa.Agent.RefreshDevicesAsync();

        _now += 1000;
        trabalho.Profile.Bookmark("https://x.example/", "X");
        trabalho.Profile.Bookmark("https://y.example/", "Y (trabalho)");
        Assert.True(await trabalho.Agent.RunAsync());
        _now += 1000;
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        await Settle(casa, trabalho);
        foreach (var device in new[] { casa, trabalho })
        {
            Assert.Equal("X", device.Profile.Title("https://x.example/"));
            Assert.Equal("Y (trabalho)", device.Profile.Title("https://y.example/"));
        }
    }

    // A troca de chave falha a meio (o registo de chaves antigo não se apaga): a conta nova já está guardada, o outro
    // aparelho continua na antiga sem parar, e a ronda seguinte acaba a troca — ninguém perde a chave.
    [Fact]
    public async Task RemoveDevice_InterruptedHalfway_FinishesOnTheNextRound_AndNobodyLosesTheKey()
    {
        var flaky = new FlakyTransport(new FolderTransport(_folder));
        var casa = NewDevice("Casa", () => flaky);
        await casa.Agent.StartAsync();
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho, portatil);
        var devices = await casa.Agent.RefreshDevicesAsync();
        var oldSecret = casa.Store.Load()!.Secret;

        flaky.FailDeletingKeys = true;
        await Assert.ThrowsAsync<SyncUserException>(() => casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id));
        var saved = casa.Store.Load()!;
        Assert.NotEqual(oldSecret, saved.Secret);   // a conta nova ficou guardada antes de mexer na antiga
        Assert.NotNull(saved.Rotation);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal(oldSecret, trabalho.Store.Load()!.Secret);

        flaky.FailDeletingKeys = false;
        Assert.True(await casa.Agent.RunAsync());
        Assert.Null(casa.Store.Load()!.Rotation);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        Assert.True(await portatil.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, portatil.Agent.Status.Phase);
    }

    // Duas remoções seguidas enquanto um aparelho estava desligado: ele segue as duas contas novas até à atual.
    [Fact]
    public async Task TwoRemovalsWhileADeviceWasOff_ItFollowsBothChanges()
    {
        var (casa, trabalho) = await TwoAsync();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        var quarto = NewDevice("Quarto");
        await quarto.Agent.StartAsync();
        await quarto.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho, portatil, quarto);
        var devices = await casa.Agent.RefreshDevicesAsync();
        Assert.Equal(4, devices.Count);

        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        // Os que ainda não passaram para a conta nova continuam na lista (à espera da chave nova).
        Assert.Contains(casa.Agent.Status.Devices, d => d.Name == "Quarto");
        await casa.Agent.RemoveDeviceAsync(casa.Agent.Status.Devices.Single(d => d.Name == "Trabalho").Id);

        casa.Profile.Bookmark("https://depois.example/", "Depois");
        Assert.True(await casa.Agent.RunAsync());
        Assert.True(await quarto.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, quarto.Agent.Status.Phase);
        Assert.Equal(casa.Store.Load()!.Secret, quarto.Store.Load()!.Secret);
        await Settle(casa, quarto);
        Assert.Equal("Depois", quarto.Profile.Title("https://depois.example/"));
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
    }

    // Um aparelho que aparece na conta só depois de o utilizador ver a lista não recebe a chave nova, e o aviso diz.
    [Fact]
    public async Task RemoveDevice_ADeviceThatShowsUpOnlyNow_DoesNotGetTheNewKey_AndIsMentioned()
    {
        var (casa, trabalho) = await TwoAsync();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho, portatil);
        var devices = await casa.Agent.RefreshDevicesAsync();
        var oldChain = casa.Store.Load()!.Engine!.ChainId;

        // O portátil (prestes a ser removido) inventa um aparelho na conta, com uma chave dele.
        using (var secret = Eteris.Sync.Crypto.AccountSecret.FromCode(casa.Agent.Status.RecoveryCode!))
        {
            var fake = await SyncEngine.JoinAsync(new FolderTransport(_folder), secret, DeviceIdentity.Create("Falso"), Now);
            fake.Put(Collections.Bookmarks, "https://isco.example/", new JsonObject { ["title"] = "isco" });
            await fake.SyncAsync();
        }
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        var rekeys = Path.Combine(_folder, "eteris", oldChain, "r");
        Assert.Single(Directory.GetDirectories(rekeys)); // só o «trabalho»
        Assert.Contains("«Falso»", casa.Agent.Status.Notice);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
    }

    // «Sair» com «apagar a chave» a meio de uma ronda: a ronda acaba depois e não volta a gravar a chave.
    [Fact]
    public async Task SignOut_ForgettingTheKey_DuringARound_DoesNotBringTheKeyBack()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        casa.Profile.Bookmark("https://a.example/", "A");
        casa.Profile.DuringCycle = () => casa.Agent.SignOut(forgetKey: true);
        await casa.Agent.RunAsync();
        Assert.Null(casa.Store.Load());
        Assert.False(casa.Agent.HasKey);
        Assert.Equal(SyncPhase.Off, casa.Agent.Status.Phase);
        await casa.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, casa.Agent.Status.Phase); // a conta existe, mas a chave já não está aqui
    }

    // Outra conta Google: pausa, sem apagar nada; de volta à conta certa, continua sem código.
    [Fact]
    public async Task OtherGoogleAccount_PausesWithoutDeletingAnything_AndResumesWithTheRightOne()
    {
        var account = "conta-a";
        var store = new MemoryStore();
        var profile = new MemoryProfile();
        var casa = new SyncAgent(() => new FolderTransport(_folder), profile, store, "Casa", Now, Quick) { AccountKey = () => account };
        await casa.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Status.Phase);
        Assert.Equal("conta-a", store.Load()!.Account);
        var secret = store.Load()!.Secret;

        casa.SignOut(forgetKey: false);
        account = "conta-b";
        await casa.StartAsync();
        Assert.Equal(SyncPhase.Stopped, casa.Status.Phase);
        Assert.Contains("outra conta Google", casa.Status.Notice);
        Assert.Equal(secret, store.Load()!.Secret);
        Assert.NotNull(store.Load()!.Engine);

        account = "conta-a";
        await casa.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Status.Phase);
        Assert.Equal(secret, store.Load()!.Secret);
    }

    // 0.2.15 (T4): a barra da janela distingue a pausa por outra conta Google das outras paragens (a fase sozinha não
    // chega, e o aviso de «conta desaparecida» também fala de «outra conta Google»).
    [Fact]
    public async Task OtherAccount_IsFlaggedInTheStatus()
    {
        // Outra paragem (a conta de sincronização desapareceu do transporte): Stopped, mas não «outra conta».
        var (gone, trabalho) = await TwoAsync();
        await gone.Agent.DeleteCloudDataAsync();
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.Contains("outra conta Google", trabalho.Agent.Status.Notice);
        Assert.False(trabalho.Agent.Status.OtherAccount);
        Assert.Equal(SyncPhase.Stopped, gone.Agent.Status.Phase);
        Assert.False(gone.Agent.Status.OtherAccount);

        // A pausa por outra conta Google (o transporte ficou vazio: a casa cria uma conta nova).
        var account = "conta-a";
        var casa = new SyncAgent(() => new FolderTransport(_folder), new MemoryProfile(), new MemoryStore(), "Casa", Now, Quick) { AccountKey = () => account };
        await casa.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Status.Phase);
        Assert.False(casa.Status.OtherAccount);
        casa.SignOut(forgetKey: false);
        account = "conta-b";
        await casa.StartAsync();
        Assert.Equal(SyncPhase.Stopped, casa.Status.Phase);
        Assert.True(casa.Status.OtherAccount);
        account = "conta-a";
        await casa.StartAsync();
        Assert.False(casa.Status.OtherAccount);
    }

    // O registo de chaves no Drive não abre (alterado ou danificado): para sem apagar a chave; voltando ao normal, retoma.
    [Fact]
    public async Task KeysThatDoNotOpen_StopWithoutDeletingTheKey()
    {
        var (casa, trabalho) = await TwoAsync();
        var chain = casa.Store.Load()!.Engine!.ChainId;
        var keys = Path.Combine(_folder, "eteris", chain, "keys");
        var good = File.ReadAllBytes(keys);
        // JSON válido, mas a DEK embrulhada alterada: não abre com esta chave.
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(good)!.AsObject();
        var dek = envelope["dek"]!.GetValue<string>();
        envelope["dek"] = (dek[0] == 'A' ? "B" : "A") + dek[1..];
        File.WriteAllBytes(keys, System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString()));

        var reopened = NewDevice("Trabalho", store: trabalho.Store, profile: trabalho.Profile);
        Assert.True(await reopened.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, reopened.Agent.Status.Phase);
        Assert.NotNull(trabalho.Store.Load()!.Secret);
        Assert.NotNull(trabalho.Store.Load()!.Engine);

        File.WriteAllBytes(keys, good);
        await reopened.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, reopened.Agent.Status.Phase);
    }

    // Numa ronda agendada um pedido de aparelho novo só se conta (nenhuma resposta escrita no Drive); responde-se quando
    // o utilizador abre o menu ou a secção Sincronização.
    [Fact]
    public async Task PairingRequests_ARoundOnlyCountsThem_TheUserOpeningTheMenuAnswers()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var novo = NewDevice("Novo");
        await novo.Agent.StartAsync();
        novo.Agent.RequestPairing();
        var pair = Path.Combine(_folder, "eteris", "pair");
        await Until(() => Directory.Exists(pair) && Directory.EnumerateFiles(pair, "0-request", SearchOption.AllDirectories).Any(), "o pedido");

        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal(1, casa.Agent.Status.WaitingRequests);
        Assert.Empty(Directory.EnumerateFiles(pair, "1-host", SearchOption.AllDirectories));

        await casa.Agent.CheckRequestsAsync();
        Assert.Single(Directory.EnumerateFiles(pair, "1-host", SearchOption.AllDirectories));
        Assert.Equal(0, casa.Agent.Status.WaitingRequests);
        novo.Agent.CancelPairing();
    }

    // Uma coleção que não se lê nesta ronda (favoritos bloqueados) não vira «apagar tudo» nos outros aparelhos.
    [Fact]
    public async Task UnreadableCollection_IsNotDeletedOnTheOtherDevices()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Profile.Bookmark("https://a.example/", "A");
        casa.Profile.Bookmark("https://b.example/", "B");
        casa.Profile.SetNotes("as notas");
        await Settle(casa, trabalho);

        trabalho.Profile.Unreadable.Add(Collections.Bookmarks);
        trabalho.Profile.Unreadable.Add(Collections.Notes);
        Assert.False(trabalho.Agent.HasLocalChanges());
        _now += 1000;
        await Settle(trabalho, casa);
        Assert.Equal("A", casa.Profile.Title("https://a.example/"));
        Assert.Equal("B", casa.Profile.Title("https://b.example/"));
        Assert.Equal("as notas", casa.Profile.Notes);

        trabalho.Profile.Unreadable.Clear();
        await Settle(trabalho, casa);
        Assert.Equal("A", casa.Profile.Title("https://a.example/"));
        Assert.Equal(casa.Profile.Fingerprint(), trabalho.Profile.Fingerprint());
    }

    // Dois computadores criam a conta ao mesmo tempo (Drive vazio para os dois): o de identificador maior sai da sua,
    // volta a «escolher» e junta-se à outra sem perder nada.
    [Fact]
    public async Task TwoComputersCreateTheAccountAtTheSameTime_OneLeaves_AndNothingIsLost()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://casa.example/", "Casa");
        await casa.Agent.StartAsync();
        var hidden = new FlakyTransport(new FolderTransport(_folder)) { HideRootOnce = true };
        var trabalho = NewDevice("Trabalho", () => hidden);
        trabalho.Profile.Bookmark("https://trabalho.example/", "Trabalho");
        await trabalho.Agent.StartAsync();
        Assert.True(await casa.Agent.RunAsync());
        Assert.True(await trabalho.Agent.RunAsync());

        var phases = new[] { casa.Agent.Status.Phase, trabalho.Agent.Status.Phase };
        Assert.Single(phases, p => p == SyncPhase.Choose);
        Assert.Single(phases, p => p == SyncPhase.On);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_folder, "eteris"), "keys", SearchOption.AllDirectories));
        var (left, stayed) = casa.Agent.Status.Phase == SyncPhase.Choose ? (casa, trabalho) : (trabalho, casa);
        Assert.Contains("ao mesmo tempo", left.Agent.Status.Notice);
        await left.Agent.JoinWithCodeAsync(stayed.Agent.Status.RecoveryCode!);
        await Settle(stayed, left);
        Assert.Equal("Casa", trabalho.Profile.Title("https://casa.example/"));
        Assert.Equal("Trabalho", casa.Profile.Title("https://trabalho.example/"));
    }

    // A autorização da Google foi retirada: a ronda falha e fica marcado que é preciso entrar outra vez (não «tenta
    // outra vez sozinho»).
    [Fact]
    public async Task RevokedAuthorization_NeedsSignIn()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        Assert.False(casa.Agent.Status.NeedsSignIn);
        google.Revoked.Add(google.RefreshToken);
        google.ExpireAccessToken();
        _now += 3_700_000;
        casa.Profile.Bookmark("https://x.example/", "X");
        Assert.False(await casa.Agent.RunAsync());
        Assert.True(casa.Agent.Status.NeedsSignIn);
        Assert.Contains("Entre outra vez", casa.Agent.Status.Error);
    }

    // O aviso de sessão expirada (T11) ligado ao agente como o SyncService o liga: pergunta uma vez por abertura.
    [Fact]
    public async Task RevokedAuthorization_DrivesThePromptOnce()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        var prompt = new Eteris.Browser.Sync.SessionExpiredPrompt();
        var asked = 0;
        casa.Agent.Changed += () => { if (prompt.Observe(casa.Agent.Status.NeedsSignIn)) asked++; };
        await casa.Agent.StartAsync();
        Assert.Equal(0, asked);
        Assert.False(prompt.Pending);

        google.Revoked.Add(google.RefreshToken);
        google.ExpireAccessToken();
        _now += 3_700_000;
        casa.Profile.Bookmark("https://x.example/", "X");
        Assert.False(await casa.Agent.RunAsync());
        Assert.True(casa.Agent.Status.NeedsSignIn);
        Assert.Equal(1, asked);
        Assert.True(prompt.Pending);

        // Outra ronda falhada (por exemplo «Sincronizar agora»): não volta a perguntar.
        casa.Profile.Bookmark("https://y.example/", "Y");
        Assert.False(await casa.Agent.RunAsync(force: true));
        Assert.Equal(1, asked);

        // Entrou outra vez (a autorização volta): a sincronização retoma e a pausa acaba.
        google.Revoked.Clear();
        await casa.Agent.StartAsync();
        Assert.False(casa.Agent.Status.NeedsSignIn);
        Assert.Null(casa.Agent.Status.Error);
        // O aviso não respondido acaba com a sessão de volta (a barra não fica à espera de resposta).
        Assert.False(prompt.Pending);

        // E se expirar outra vez na mesma abertura, fica calado e a barra não volta.
        google.Revoked.Add(google.RefreshToken);
        google.ExpireAccessToken();
        _now += 3_700_000;
        casa.Profile.Bookmark("https://z.example/", "Z");
        Assert.False(await casa.Agent.RunAsync());
        Assert.True(casa.Agent.Status.NeedsSignIn);
        Assert.Equal(1, asked);
        Assert.False(prompt.Pending);
    }

    // «Sair» sem apagar a chave, com o código de recuperação ainda por guardar: continua à vista ao voltar.
    [Fact]
    public async Task SignOut_KeepingTheKey_TheRecoveryCodeStaysUntilSaved()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var code = casa.Agent.Status.RecoveryCode;
        Assert.NotNull(code);
        casa.Agent.SignOut(forgetKey: false);
        await casa.Agent.StartAsync();
        Assert.Equal(code, casa.Agent.Status.RecoveryCode);
    }

    // «Gerar um código de recuperação novo»: o antigo deixa de servir, ninguém sai da conta.
    [Fact]
    public async Task NewRecoveryCode_TheOldOneStopsWorking_EveryoneStays()
    {
        var (casa, trabalho) = await TwoAsync();
        var old = casa.Agent.Status.RecoveryCode!;
        casa.Agent.AcknowledgeCode();
        await casa.Agent.RefreshDevicesAsync();
        await casa.Agent.NewRecoveryCodeAsync();
        var fresh = casa.Agent.Status.RecoveryCode;
        Assert.NotNull(fresh);
        Assert.NotEqual(old, fresh);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        var outro = NewDevice("Outro");
        await outro.Agent.StartAsync();
        await Assert.ThrowsAsync<SyncUserException>(() => outro.Agent.JoinWithCodeAsync(old));
        await outro.Agent.JoinWithCodeAsync(fresh!);
        Assert.Equal(SyncPhase.On, outro.Agent.Status.Phase);
    }

    // A página de fim da entrada lê a fase logo que StartAsync devolve a tarefa: tem de ler já «Starting», nunca a fase
    // da ronda anterior (um «On» antigo dava «Pronto.» antes de se saber como ficou).
    [Fact]
    public async Task StartAsync_IsAlreadyStarting_BeforeItsFirstAwait()
    {
        var gated = new GatedTransport(new FolderTransport(_folder));
        var casa = NewDevice("Casa", () => gated);
        await casa.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gated.Gate = held.Task;
        var start = casa.Agent.StartAsync(false);
        Assert.Equal(SyncPhase.Starting, casa.Agent.Status.Phase);
        held.SetResult();
        await start;
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
    }

    // Transporte que só lista depois de «Gate» acabar: segura a ronda a meio.
    private sealed class GatedTransport(ISyncTransport inner) : ISyncTransport
    {
        public Task Gate { get; set; } = Task.CompletedTask;

        public string Name => inner.Name;

        public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken);
            return await inner.ListAsync(prefix, cancellationToken);
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);
    }

    // Transporte que falha de propósito: não apaga o registo de chaves (troca de chave interrompida) ou esconde a pasta
    // na primeira listagem (dois computadores a criar a conta ao mesmo tempo).
    private sealed class FlakyTransport(ISyncTransport inner) : ISyncTransport
    {
        public bool FailDeletingKeys { get; set; }
        public bool HideRootOnce { get; set; }

        public string Name => inner.Name;

        public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            if (HideRootOnce && prefix == SyncPaths.Root)
            {
                HideRootOnce = false;
                return [];
            }
            return await inner.ListAsync(prefix, cancellationToken);
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
            => FailDeletingKeys && path.EndsWith("/keys", StringComparison.Ordinal) ? throw new IOException("falha de propósito") : inner.DeleteAsync(path, cancellationToken);
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
