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

// Os aparelhos de cada teste partilham um transporte em memória (SyncTestCloud: sem disco — uma pasta real em %TEMP% dá
// erros passageiros com a bateria em paralelo) ou o Drive falso; as esperas acordam com sinais (Changed dos agentes e
// cada escrita no transporte), nunca com um orçamento de tempo, e no fim todos os agentes param. No disco só ficam os
// tokens da sessão falsa (o TokenStore é um ficheiro).
public sealed class SyncAgentTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-sync-agent-" + Guid.NewGuid().ToString("N"));
    private readonly SyncTestCloud _cloud = new();

    private long Now() => _now;

    public void Dispose()
    {
        _cloud.StopAll();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store);

    private Device NewDevice(string name, Func<ISyncTransport>? transport = null, MemoryStore? store = null, MemoryProfile? profile = null)
    {
        profile ??= new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport ?? (() => _cloud), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) };
        _cloud.Watch(name, agent);
        return new Device(agent, profile, store);
    }

    private Task Until(Func<bool> condition, string what) => _cloud.WaitFor(condition, what);

    // O pedido de um computador novo já está no transporte: o anfitrião procura uma vez (o 1-host fica escrito antes de
    // CheckRequestsAsync voltar) e espera-se pelo pedido pronto.
    private async Task AnswerUntil(Device host, Func<bool> ready, string what)
    {
        await Until(() => _cloud.PairingFiles(Eteris.Sync.Pairing.PairingProtocol.Request) > 0, "o pedido");
        await host.Agent.CheckRequestsAsync();
        await Until(ready, what);
    }

    private static async Task Settle(params Device[] devices)
    {
        for (var round = 0; round < 3; round++)
            foreach (var device in devices)
                Assert.True(await device.Agent.RunAsync());
    }

    // ---- criar e entrar ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstDevice_CreatesTheAccount_AsksForNothing_KitIsPending()
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
        Assert.True(status.LastSuccessMs > 0);
        // Nada é pedido ao criar a conta: o kit fica por guardar, neste PC, e vê-se quando se quiser.
        Assert.Equal(KitState.Pending, status.Kit!.State);
        Assert.Equal(_now, status.Kit.SinceMs);
        var kit = casa.Agent.RevealKit()!;
        Assert.Equal(24, kit.Words.Split(' ').Length);

        // Reabrir o Éteris: o mesmo kit (a mesma impressão digital e as mesmas palavras).
        var reopened = NewDevice("Casa", store: casa.Store, profile: casa.Profile);
        Assert.Equal(status.Kit.Id, reopened.Agent.Status.Kit!.Id);
        Assert.Equal(kit.Words, reopened.Agent.RevealKit()!.Words);
        reopened.Agent.MarkKitSaved();
        Assert.Equal(KitState.Saved, reopened.Agent.Status.Kit!.State);
        var again = NewDevice("Casa", store: casa.Store, profile: casa.Profile);
        Assert.Equal(KitState.Saved, again.Agent.Status.Kit!.State);
        // «Ver de novo»: guardado não quer dizer escondido para sempre.
        Assert.Equal(kit.Words, again.Agent.RevealKit()!.Words);

        // Nenhum texto do perfil em claro no transporte (só ficheiros cifrados).
        Assert.NotEmpty(_cloud.Memory.Files);
        foreach (var (_, data) in _cloud.Memory.Files)
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
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
        var code = casa.Agent.RevealKit()!.Words;

        var trabalho = NewDevice("Trabalho");
        trabalho.Profile.Bookmark("https://so-no-trabalho.example/", "Só aqui");
        trabalho.Profile.Bookmark("https://comum.example/", "Título local");
        trabalho.Profile.Setting("theme", "dark");
        trabalho.Profile.SetNotes("do trabalho");
        await trabalho.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, trabalho.Agent.Status.Phase);
        Assert.Null(trabalho.Agent.Status.Kit);

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
    public async Task Notes_RemoteEditNotYetApplied_IsMergedWithTheNextLocalEdit()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Profile.SetNotes("linha 1\nlinha 2\nlinha 3");
        await Settle(casa, trabalho);
        casa.Profile.SetNotes("linha 1 (casa)\nlinha 2\nlinha 3");
        Assert.True(await casa.Agent.RunAsync());
        _now += 1000;
        // O trabalho está a escrever nas notas: a ronda traz a versão da casa e não a aplica.
        trabalho.Profile.Refuse = c => c.Collection == Collections.Notes;
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal("linha 1\nlinha 2\nlinha 3", trabalho.Profile.Notes);
        trabalho.Profile.Refuse = _ => false;
        trabalho.Profile.SetNotes("linha 1\nlinha 2\nlinha 3 (trabalho)");
        _now += 1000;
        await Settle(trabalho, casa);
        Assert.Equal("linha 1 (casa)\nlinha 2\nlinha 3 (trabalho)", casa.Profile.Notes);
        Assert.Equal(casa.Profile.Notes, trabalho.Profile.Notes);
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
        using (var secret = Eteris.Sync.Crypto.AccountSecret.FromCode(casa.Agent.RevealKit()!.Words))
        {
            var newer = await SyncEngine.JoinAsync(_cloud, secret, DeviceIdentity.Create("Mais recente"), Now);
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
        casa.Agent.MarkKitSaved();

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
        // Entrou por aprovação: não se sabe se tem o kit guardado noutro lado — fica por guardar (só no menu).
        Assert.Equal(KitState.Pending, portatil.Agent.Status.Kit!.State);
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
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1, "o pedido no anfitrião");
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
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        await Settle(casa, trabalho, portatil);
        await trabalho.Agent.RefreshDevicesAsync(); // o «trabalho» fixa o «portátil»: o caso de três aparelhos
        var devices = await casa.Agent.RefreshDevicesAsync();
        Assert.Equal(3, devices.Count);
        Assert.True(devices[0].IsThis);
        var oldCode = casa.Store.Load()!.Secret;

        trabalho.Profile.Bookmark("https://antes.example/", "Mudado antes de saber"); // ainda por enviar
        var removed = devices.Single(d => d.Name == "Portátil");
        await casa.Agent.RemoveDeviceAsync(removed.Id);
        Assert.NotEqual(KitState.Saved, casa.Agent.Status.Kit!.State); // kit novo, por guardar
        Assert.NotEqual(oldCode, casa.Store.Load()!.Secret);
        Assert.Contains("Portátil", casa.Agent.Status.Notice);

        // O que fica recebe a conta nova depois de confirmar, e o que tinha por enviar segue.
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.True(trabalho.Agent.Status.RekeyPending);            // três aparelhos: pede o identificador do kit
        Assert.Equal(SyncAgent.RekeyHeldText, trabalho.Agent.Status.Notice);
        await trabalho.Agent.ConfirmRekeyAsync(" " + casa.Agent.Status.Kit!.Id.ToLowerInvariant() + " ");
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Contains("mudou de chave em «Casa»", trabalho.Agent.Status.Notice);
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

    // ---- a mensagem r/ de um aparelho removido (SYN-01) ---------------------------------------------------------------

    // Três aparelhos na conta, cada um com as chaves dos outros dois fixadas.
    private async Task<(Device Casa, Device Trabalho, Device Portatil)> ThreeAsync()
    {
        var (casa, trabalho) = await TwoAsync();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        await Settle(casa, trabalho, portatil);
        foreach (var device in new[] { casa, trabalho, portatil })
            Assert.Equal(3, (await device.Agent.RefreshDevicesAsync()).Count);
        Assert.Equal(2, trabalho.Store.Load()!.Engine!.PeerKeys.Count);
        return (casa, trabalho, portatil);
    }

    // A ronda de quem ficou depois de uma troca de chave: se ficou em pausa, confirma com o identificador do kit de quem trocou.
    private static async Task FollowRekeyAsync(Device stayed, Device rotated)
    {
        Assert.True(await stayed.Agent.RunAsync());
        if (stayed.Agent.Status.RekeyPending) await stayed.Agent.ConfirmRekeyAsync(rotated.Agent.Status.Kit!.Id);
        Assert.Equal(SyncPhase.On, stayed.Agent.Status.Phase);
    }

    // A conta do atacante: o identificador do kit dela não coincide, por acaso (1 em 1 048 576), com o da conta verdadeira.
    private static Eteris.Sync.Crypto.AccountSecret Hijack(string realKitId)
    {
        while (true)
        {
            var secret = Eteris.Sync.Crypto.AccountSecret.Create();
            if (secret.KitId != realKitId) return secret;
            secret.Dispose();
        }
    }

    private static string SecretOf(Eteris.Sync.Crypto.AccountSecret secret) => Eteris.Sync.Crypto.Kdf.Base64Url(secret.ToBytes());

    // O removido apagou a mensagem verdadeira (r/B/A) e deixou a dele (r/B/P) com uma conta sua; depois, a ronda do
    // «trabalho».
    private async Task<(Device Casa, Device Trabalho, Eteris.Sync.Crypto.AccountSecret Hijack)> HijackedByDeletingTheRealMessageAsync()
    {
        var (casa, trabalho, portatil) = await ThreeAsync();
        var devices = await casa.Agent.RefreshDevicesAsync();
        var old = casa.Store.Load()!.Engine!;
        var stolen = portatil.Store.Load()!.Engine!;          // o estado do removido: a chave dele e as fixadas
        var idB = trabalho.Store.Load()!.Engine!.Device.Id;
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);

        await _cloud.DeleteAsync(SyncPaths.Rekey(old.ChainId, idB, old.Device.Id));
        var hijack = Hijack(casa.Agent.Status.Kit!.Id);
        await SyncEngine.CreateAsync(_cloud, hijack, stolen.Device, Now);
        await SyncEngine.FinishRotationAsync(_cloud, stolen, hijack,
            new RotationPending { OldChain = old.ChainId, Staying = new Dictionary<string, string> { [idB] = stolen.PeerKeys[idB] } });

        Assert.True(await trabalho.Agent.RunAsync());
        return (casa, trabalho, hijack);
    }

    [Fact]
    public async Task RemovedDevice_DeletesTheRealMessage_OthersDoNotFollow()
    {
        var (_, trabalho, taken) = await HijackedByDeletingTheRealMessageAsync();
        using var hijack = taken;
        var idB = trabalho.Store.Load()!.Engine!.Device.Id;

        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.True(trabalho.Agent.Status.RekeyPending);
        Assert.Equal(SyncAgent.RekeyHeldText, trabalho.Agent.Status.Notice);
        Assert.NotEqual(SecretOf(hijack), trabalho.Store.Load()!.Secret);
        Assert.NotNull(trabalho.Store.Load()!.Engine);          // a pausa não deita fora o estado nem a base
        Assert.NotNull(trabalho.Store.Load()!.Baseline);
        Assert.Empty(_cloud.Paths(SyncPaths.Batches(hijack.ChainId) + idB + "/"));
        Assert.False(_cloud.Has(SyncPaths.State(hijack.ChainId, idB)));
        // Reabrir o Éteris a meio: a pausa continua.
        Assert.True(NewDevice("Trabalho", store: trabalho.Store, profile: trabalho.Profile).Agent.Status.RekeyPending);
    }

    // A variante sem apagar a mensagem verdadeira: o removido apaga o registo de chaves da conta nova e deixa a
    // mensagem dele lá dentro (o segundo salto).
    [Fact]
    public async Task RemovedDevice_DeletesTheKeysOfTheNewAccount_OthersDoNotFollow()
    {
        var (casa, trabalho, portatil) = await ThreeAsync();
        var devices = await casa.Agent.RefreshDevicesAsync();
        var stolen = portatil.Store.Load()!.Engine!;
        var idB = trabalho.Store.Load()!.Engine!.Device.Id;
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);

        var newChain = casa.Store.Load()!.Engine!.ChainId;
        using var hijack = Hijack(casa.Agent.Status.Kit!.Id);
        await SyncEngine.CreateAsync(_cloud, hijack, stolen.Device, Now);
        await SyncEngine.FinishRotationAsync(_cloud, stolen, hijack,
            new RotationPending { OldChain = newChain, Staying = new Dictionary<string, string> { [idB] = stolen.PeerKeys[idB] } });

        Assert.True(await trabalho.Agent.RunAsync());
        Assert.False(_cloud.Has(SyncPaths.Keys(newChain)));
        Assert.True(trabalho.Agent.Status.RekeyPending);
        Assert.NotEqual(SecretOf(hijack), trabalho.Store.Load()!.Secret);
        // A conta que o segundo salto encontra é a do removido, não a do identificador.
        await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id));
        Assert.True(trabalho.Agent.Status.RekeyPending);          // um identificador errado não tira a pausa
        Assert.NotEqual(SecretOf(hijack), trabalho.Store.Load()!.Secret);
        Assert.Empty(_cloud.Paths(SyncPaths.Batches(hijack.ChainId) + idB + "/"));
    }

    // Um transporte que deixa o teste meter-se antes de cada listagem e de cada leitura (lançar, ou agir a meio).
    private sealed class HookedTransport(ISyncTransport inner) : ISyncTransport
    {
        public Action<string>? OnList { get; set; }
        public Action<string>? OnRead { get; set; }

        public string Name => inner.Name;

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            OnList?.Invoke(prefix);
            return inner.ListAsync(prefix, cancellationToken);
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            OnRead?.Invoke(path);
            return inner.ReadAsync(path, cancellationToken);
        }

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);
    }

    // O «trabalho» em pausa depois de a «casa» remover o «portátil», reaberto com um transporte em que o teste se mete.
    private async Task<(Device Casa, Device Trabalho, HookedTransport Hooked)> HeldWithHooksAsync()
    {
        var (casa, trabalho, _) = await ThreeAsync();
        var devices = await casa.Agent.RefreshDevicesAsync();
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.True(trabalho.Agent.Status.RekeyPending);
        var hooked = new HookedTransport(_cloud);
        var reopened = NewDevice("Trabalho", () => hooked, trabalho.Store, trabalho.Profile);
        Assert.True(reopened.Agent.Status.RekeyPending);
        return (casa, reopened, hooked);
    }

    // Revisão (I1): «Não tenho o identificador» enquanto a confirmação lia o transporte, e a leitura falha a seguir. A
    // falha não repõe a pausa por cima da desistência: fica o alerta «conta substituída», não um «escreva o
    // identificador» sem caixa.
    [Fact]
    public async Task Rekey_AbandonedWhileConfirming_AFailedReadDoesNotBringThePauseBack()
    {
        var (casa, trabalho, hooked) = await HeldWithHooksAsync();
        hooked.OnList = prefix =>
        {
            if (!prefix.Contains("/r/", StringComparison.Ordinal)) return;
            trabalho.Agent.AbandonRekey();
            throw new IOException("pasta inacessível");
        };
        await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id));

        var status = trabalho.Agent.Status;
        Assert.Equal(SyncPhase.Stopped, status.Phase);
        Assert.False(status.RekeyPending);
        Assert.True(status.ChainReplaced);
        Assert.NotEqual(SyncAgent.RekeyHeldText, status.Notice);
        Assert.NotEqual(SyncAgent.RekeyHeldText, trabalho.Store.Load()!.Stopped);
    }

    // Uma leitura que falha antes de o identificador ser conferido deixa a pausa como estava (nada a repor).
    [Fact]
    public async Task Rekey_AFailedReadWhileConfirming_KeepsThePause()
    {
        var (casa, trabalho, hooked) = await HeldWithHooksAsync();
        var saves = trabalho.Store.Saves;
        hooked.OnList = prefix =>
        {
            if (prefix.Contains("/r/", StringComparison.Ordinal)) throw new IOException("pasta inacessível");
        };
        await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id));
        Assert.True(trabalho.Agent.Status.RekeyPending);
        Assert.Equal(saves, trabalho.Store.Saves);
        hooked.OnList = null;
        await trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
    }

    // Revisão (M2): com o identificador certo, a entrada na conta nova é cancelada (não é uma falha passageira). A
    // pausa volta a ficar como estava, em vez de a memória ficar «a ligar» sem conta nenhuma.
    [Fact]
    public async Task Rekey_CancelledAfterTheRightIdentifier_GoesBackToThePause()
    {
        var (casa, trabalho, hooked) = await HeldWithHooksAsync();
        var keys = SyncPaths.Keys(casa.Store.Load()!.Engine!.ChainId);
        var reads = 0;
        hooked.OnRead = path =>
        {
            // A primeira leitura do registo de chaves da conta nova é a de quem procura a mensagem; a segunda, a de quem entra.
            if (path == keys && ++reads > 1) throw new OperationCanceledException(new CancellationToken(canceled: true));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id));

        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.True(trabalho.Agent.Status.RekeyPending);
        Assert.NotEqual(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        hooked.OnRead = null;
        await trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
    }

    // ---- o removido repõe o registo de chaves da cadeia antiga (revisão do SYN-01, I2) --------------------------------

    // A conta só «desaparece» quando falta o registo de chaves. O removido tem o segredo antigo e, enquanto tiver
    // acesso ao Drive, pode voltar a pôr lá o registo antigo: quem ficou não daria pela troca e continuava a sincronizar
    // com ele na cadeia antiga. A mensagem r/ que quem trocou a chave lhe deixou conta como a troca.
    [Fact]
    public async Task RemovedDevice_RestoresTheOldKeys_OthersStillFindTheRekey()
    {
        var (casa, trabalho, portatil) = await ThreeAsync();
        var devices = await casa.Agent.RefreshDevicesAsync();
        var oldChain = casa.Store.Load()!.Engine!.ChainId;
        var oldKeys = _cloud.Memory.Files[SyncPaths.Keys(oldChain)].ToArray();   // o removido guardou uma cópia
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        Assert.False(_cloud.Has(SyncPaths.Keys(oldChain)));
        await _cloud.WriteAsync(SyncPaths.Keys(oldChain), oldKeys);

        _now += 1000;
        trabalho.Profile.Bookmark("https://do-trabalho.example/", "Do trabalho, depois da remoção");
        Assert.True(await trabalho.Agent.RunAsync());
        // Três aparelhos fixados: a mensagem não é inequívoca, e o «trabalho» pede o identificador do kit novo.
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.True(trabalho.Agent.Status.RekeyPending);

        // O removido continua sozinho na cadeia antiga: não recebe nada do «trabalho», e o que escreve não chega a ninguém.
        _now += 1000;
        portatil.Profile.Bookmark("https://do-portatil.example/", "Do portátil, depois de removido");
        Assert.True(await portatil.Agent.RunAsync());
        await trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        casa.Profile.Bookmark("https://da-casa.example/", "Da casa");
        await Settle(casa, trabalho);
        Assert.True(await portatil.Agent.RunAsync());
        Assert.Equal("Do trabalho, depois da remoção", casa.Profile.Title("https://do-trabalho.example/"));
        Assert.Equal("Da casa", trabalho.Profile.Title("https://da-casa.example/"));
        Assert.Null(portatil.Profile.Title("https://do-trabalho.example/"));
        Assert.Null(trabalho.Profile.Title("https://do-portatil.example/"));
        Assert.Null(casa.Profile.Title("https://do-portatil.example/"));
        Assert.Empty(_cloud.Paths(SyncPaths.Batches(oldChain) + trabalho.Store.Load()!.Engine!.Device.Id + "/"));
    }

    // O mesmo com um «trabalho» que só fixou a «casa»: segue sozinho para a conta nova. E uma mensagem já tratada não
    // volta a disparar nada: as rondas seguintes ficam na conta nova, mesmo que o registo antigo volte a aparecer.
    [Fact]
    public async Task RemovedDevice_RestoresTheOldKeys_ADeviceThatOnlyPinnedTheSenderFollowsAlone_AndOnlyOnce()
    {
        var (casa, trabalho) = await TwoAsync();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        await Settle(casa, trabalho, portatil);
        var devices = await casa.Agent.RefreshDevicesAsync();
        Assert.Single(trabalho.Store.Load()!.Engine!.PeerKeys);
        var oldChain = casa.Store.Load()!.Engine!.ChainId;
        var oldKeys = _cloud.Memory.Files[SyncPaths.Keys(oldChain)].ToArray();
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        await _cloud.WriteAsync(SyncPaths.Keys(oldChain), oldKeys);

        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.False(trabalho.Agent.Status.RekeyPending);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        Assert.Contains("mudou de chave em «Casa»", trabalho.Agent.Status.Notice);

        // Rondas seguintes: nada volta a disparar (o registo antigo reposto outra vez não interessa a quem já seguiu).
        await _cloud.WriteAsync(SyncPaths.Keys(oldChain), oldKeys);
        casa.Profile.Bookmark("https://da-casa.example/", "Da casa");
        await Settle(casa, trabalho);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.False(trabalho.Agent.Status.RekeyPending);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        Assert.Equal("Da casa", trabalho.Profile.Title("https://da-casa.example/"));
        Assert.Empty(_cloud.Paths(SyncPaths.Rekeys(casa.Store.Load()!.Engine!.ChainId, trabalho.Store.Load()!.Engine!.Device.Id)));
    }

    // Um ficheiro em r/<este>/ que não é uma mensagem válida (lixo, ou de um aparelho sem chave fixada) não para a
    // ronda nem tira o aparelho da conta: quem só consegue escrever no Drive não desliga ninguém.
    [Fact]
    public async Task AJunkRekeyFile_DoesNotStopTheRound()
    {
        var (casa, trabalho) = await TwoAsync();
        var state = trabalho.Store.Load()!.Engine!;
        await _cloud.WriteAsync(SyncPaths.Rekey(state.ChainId, state.Device.Id, "aparelho-inventado"), [1, 2, 3]);
        await _cloud.WriteAsync(SyncPaths.Rekey(state.ChainId, state.Device.Id, casa.Store.Load()!.Engine!.Device.Id), [1, 2, 3]);
        casa.Profile.Bookmark("https://da-casa.example/", "Da casa");
        await Settle(casa, trabalho);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Null(trabalho.Agent.Status.Error);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        Assert.Equal("Da casa", trabalho.Profile.Title("https://da-casa.example/"));
    }

    // O removido repõe o registo antigo e troca a mensagem verdadeira por uma sua: o «trabalho» cai na pausa e não o segue.
    [Fact]
    public async Task RemovedDevice_RestoresTheOldKeys_AndSwapsTheMessage_OthersDoNotFollow()
    {
        var (casa, trabalho, portatil) = await ThreeAsync();
        var devices = await casa.Agent.RefreshDevicesAsync();
        var old = casa.Store.Load()!.Engine!;
        var oldKeys = _cloud.Memory.Files[SyncPaths.Keys(old.ChainId)].ToArray();
        var stolen = portatil.Store.Load()!.Engine!;
        var idB = trabalho.Store.Load()!.Engine!.Device.Id;
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);

        await _cloud.DeleteAsync(SyncPaths.Rekey(old.ChainId, idB, old.Device.Id));
        using var hijack = Hijack(casa.Agent.Status.Kit!.Id);
        await SyncEngine.CreateAsync(_cloud, hijack, stolen.Device, Now);
        await SyncEngine.FinishRotationAsync(_cloud, stolen, hijack,
            new RotationPending { OldChain = old.ChainId, Staying = new Dictionary<string, string> { [idB] = stolen.PeerKeys[idB] } });
        await _cloud.WriteAsync(SyncPaths.Keys(old.ChainId), oldKeys);

        trabalho.Profile.Bookmark("https://do-trabalho.example/", "Do trabalho, depois da remoção");
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.True(trabalho.Agent.Status.RekeyPending);
        Assert.NotEqual(SecretOf(hijack), trabalho.Store.Load()!.Secret);
        await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id));
        Assert.True(trabalho.Agent.Status.RekeyPending);
        Assert.Empty(_cloud.Paths(SyncPaths.Batches(hijack.ChainId) + idB + "/"));
        Assert.Empty(_cloud.Paths(SyncPaths.Batches(old.ChainId) + idB + "/"));
    }

    [Fact]
    public async Task Rekey_ThreeWrongIdentifiers_StopForGood_AndRejoiningByKitWorks()
    {
        var (casa, trabalho, taken) = await HijackedByDeletingTheRealMessageAsync();
        using var hijack = taken;
        Assert.True(trabalho.Agent.Status.RekeyPending);

        // Um identificador que nem tem 4 caracteres não conta como erro.
        await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.ConfirmRekeyAsync("AB"));
        for (var wrong = 0; wrong < 2; wrong++)
        {
            await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id));
            Assert.True(trabalho.Agent.Status.RekeyPending);
        }
        await Assert.ThrowsAsync<SyncUserException>(() => trabalho.Agent.ConfirmRekeyAsync(casa.Agent.Status.Kit!.Id));
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.False(trabalho.Agent.Status.RekeyPending);
        Assert.Null(trabalho.Store.Load()!.Engine);
        Assert.NotEqual(SecretOf(hijack), trabalho.Store.Load()!.Secret);

        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
    }

    [Fact]
    public async Task DeleteCloudData_RemovesEveryFile_AndStopsEverywhere_WithoutLosingLocalData()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Profile.Bookmark("https://fica.example/", "Fica");
        await Settle(casa, trabalho);
        await casa.Agent.DeleteCloudDataAsync();
        Assert.Empty(_cloud.Memory.Files);
        Assert.Equal(SyncPhase.Stopped, casa.Agent.Status.Phase);
        Assert.Contains("apagados", casa.Agent.Status.Notice);
        Assert.Equal("Fica", casa.Profile.Title("https://fica.example/"));

        // O outro aparelho para (não volta a criar a conta sozinho: isso desfazia o «apagar»).
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
        Assert.Empty(_cloud.Memory.Files);
        Assert.Equal("Fica", trabalho.Profile.Title("https://fica.example/"));

        // «Ligar outra vez» cria uma conta nova.
        await casa.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
        Assert.NotEqual(KitState.Saved, casa.Agent.Status.Kit!.State);
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
        Assert.NotEqual(KitState.Saved, reaberta.Agent.Status.Kit!.State);
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
        var preso = new StuckTransport(_cloud, deletesBeforeStuck: 2);
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

    // Os ficheiros do transporte, pelo nome («eteris/<cadeia>/…»).
    private List<string> FilesInFolder() => [.. _cloud.Paths(string.Empty)];

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
        await outro.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
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
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        casa.Profile.Bookmark("https://y.example/", "Y");
        await Settle(casa, trabalho, portatil);
        var devices = await casa.Agent.RefreshDevicesAsync();

        _now += 1000;
        trabalho.Profile.Bookmark("https://x.example/", "X");
        trabalho.Profile.Bookmark("https://y.example/", "Y (trabalho)");
        Assert.True(await trabalho.Agent.RunAsync());
        _now += 1000;
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        await FollowRekeyAsync(trabalho, casa); // o «trabalho» leu um lote do «portátil»: fixou-o, e pede o identificador
        await Settle(casa, trabalho);
        foreach (var device in new[] { casa, trabalho })
        {
            Assert.Equal("X", device.Profile.Title("https://x.example/"));
            Assert.Equal("Y (trabalho)", device.Profile.Title("https://y.example/"));
        }
    }

    // A troca de chave falha a meio (o registo de chaves antigo não se apaga): a conta nova já está guardada e a mensagem
    // r/ já está escrita, por isso o outro aparelho segue logo para a conta nova, sem parar (o registo antigo ainda
    // presente não o prende na cadeia antiga: revisão do SYN-01, I2); a ronda seguinte acaba a troca — ninguém perde a
    // chave.
    [Fact]
    public async Task RemoveDevice_InterruptedHalfway_FinishesOnTheNextRound_AndNobodyLosesTheKey()
    {
        var flaky = new FlakyTransport(_cloud);
        var casa = NewDevice("Casa", () => flaky);
        await casa.Agent.StartAsync();
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
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
        Assert.Equal(saved.Secret, trabalho.Store.Load()!.Secret);   // já na conta nova (só fixou a «casa»: segue sozinho)

        flaky.FailDeletingKeys = false;
        Assert.True(await casa.Agent.RunAsync());
        Assert.Null(casa.Store.Load()!.Rotation);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);
        Assert.True(await portatil.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, portatil.Agent.Status.Phase);
    }

    // Duas remoções com o «trabalho» desligado, a segunda interrompida antes de o registo de chaves da conta do meio
    // sair: o «trabalho» entra na conta do meio, onde já há outra mensagem r/ para ele. Não é uma conta desaparecida:
    // fica nela, e a ronda seguinte segue a mensagem até à conta atual — sem parar nem dizer «conta substituída».
    [Fact]
    public async Task TwoRemovals_TheSecondInterrupted_ADeviceThatWasOffEndsInTheLastAccount_WithoutStopping()
    {
        var flaky = new FlakyTransport(_cloud);
        var casa = NewDevice("Casa", () => flaky);
        await casa.Agent.StartAsync();
        var code = casa.Agent.RevealKit()!.Words;
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(code);
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(code);
        var quarto = NewDevice("Quarto");
        await quarto.Agent.StartAsync();
        await quarto.Agent.JoinWithCodeAsync(code);
        await Settle(casa, trabalho, portatil, quarto);
        var devices = await casa.Agent.RefreshDevicesAsync();
        Assert.Single(trabalho.Store.Load()!.Engine!.PeerKeys);   // só fixou a «casa»: segue sozinho

        trabalho.Profile.Bookmark("https://antes.example/", "Mudado antes de saber");
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        var middle = casa.Store.Load()!.Engine!.ChainId;
        flaky.FailDeletingKeys = true;
        await Assert.ThrowsAsync<SyncUserException>(() => casa.Agent.RemoveDeviceAsync(casa.Agent.Status.Devices.Single(d => d.Name == "Quarto").Id));
        Assert.NotNull(casa.Store.Load()!.Rotation);
        Assert.True(_cloud.Has(SyncPaths.Keys(middle)));           // o registo da conta do meio ficou

        for (var round = 0; round < 2; round++)
        {
            Assert.True(await trabalho.Agent.RunAsync());
            Assert.NotEqual(SyncPhase.Stopped, trabalho.Agent.Status.Phase);
            Assert.False(trabalho.Agent.Status.ChainReplaced);
            Assert.NotNull(trabalho.Store.Load()!.Baseline);
        }
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal(casa.Store.Load()!.Secret, trabalho.Store.Load()!.Secret);

        flaky.FailDeletingKeys = false;
        casa.Profile.Bookmark("https://depois.example/", "Depois");
        await Settle(casa, trabalho);
        Assert.Null(casa.Store.Load()!.Rotation);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        Assert.Equal("Depois", trabalho.Profile.Title("https://depois.example/"));
        Assert.Equal("Mudado antes de saber", casa.Profile.Title("https://antes.example/"));
    }

    // Duas remoções seguidas enquanto um aparelho estava desligado: ele segue as duas contas novas até à atual.
    [Fact]
    public async Task TwoRemovalsWhileADeviceWasOff_ItFollowsBothChanges()
    {
        var (casa, trabalho) = await TwoAsync();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        var quarto = NewDevice("Quarto");
        await quarto.Agent.StartAsync();
        await quarto.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        await Settle(casa, trabalho, portatil, quarto);
        var devices = await casa.Agent.RefreshDevicesAsync();
        Assert.Equal(4, devices.Count);

        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        // Os que ainda não passaram para a conta nova continuam na lista (à espera da chave nova).
        Assert.Contains(casa.Agent.Status.Devices, d => d.Name == "Quarto");
        await casa.Agent.RemoveDeviceAsync(casa.Agent.Status.Devices.Single(d => d.Name == "Trabalho").Id);

        casa.Profile.Bookmark("https://depois.example/", "Depois");
        Assert.True(await casa.Agent.RunAsync());
        await FollowRekeyAsync(quarto, casa); // o «quarto» fixou os outros três: confirma com o identificador do kit final
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
        await portatil.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        await Settle(casa, trabalho, portatil);
        var devices = await casa.Agent.RefreshDevicesAsync();
        var oldChain = casa.Store.Load()!.Engine!.ChainId;

        // O portátil (prestes a ser removido) inventa um aparelho na conta, com uma chave dele.
        using (var secret = Eteris.Sync.Crypto.AccountSecret.FromCode(casa.Agent.RevealKit()!.Words))
        {
            var fake = await SyncEngine.JoinAsync(_cloud, secret, DeviceIdentity.Create("Falso"), Now);
            fake.Put(Collections.Bookmarks, "https://isco.example/", new JsonObject { ["title"] = "isco" });
            await fake.SyncAsync();
        }
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        var rekeys = SyncPaths.Root + oldChain + "/r/";
        Assert.Single(_cloud.Paths(rekeys).Select(p => p[rekeys.Length..].Split('/')[0]).Distinct()); // só o «trabalho»
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
        var casa = new SyncAgent(() => _cloud, profile, store, "Casa", Now, Quick) { AccountKey = () => account };
        _cloud.Watch("Casa", casa);
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
        var casa = new SyncAgent(() => _cloud, new MemoryProfile(), new MemoryStore(), "Casa", Now, Quick) { AccountKey = () => account };
        _cloud.Watch("Casa", casa);
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
        var keys = SyncPaths.Keys(chain);
        var good = _cloud.Memory.Files[keys];
        // JSON válido, mas a DEK embrulhada alterada: não abre com esta chave.
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(good)!.AsObject();
        var dek = envelope["dek"]!.GetValue<string>();
        envelope["dek"] = (dek[0] == 'A' ? "B" : "A") + dek[1..];
        _cloud.Memory.Replace(keys, System.Text.Encoding.UTF8.GetBytes(envelope.ToJsonString()));

        var reopened = NewDevice("Trabalho", store: trabalho.Store, profile: trabalho.Profile);
        Assert.True(await reopened.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, reopened.Agent.Status.Phase);
        Assert.NotNull(trabalho.Store.Load()!.Secret);
        Assert.NotNull(trabalho.Store.Load()!.Engine);

        _cloud.Memory.Replace(keys, good);
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
        await Until(() => _cloud.PairingFiles("0-request") == 1, "o pedido");

        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal(1, casa.Agent.Status.WaitingRequests);
        Assert.Equal(0, _cloud.PairingFiles("1-host"));

        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(1, _cloud.PairingFiles("1-host"));
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

    // Auditoria, SYN-10: um Escudo que não se leu nesta ronda não apaga as regras por site nos outros computadores.
    [Fact]
    public async Task UnreadableShield_DoesNotDeleteTheSiteRulesOnTheOtherDevices()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Profile.Set(Collections.Shield, "rule|exemplo.test|rastreador.test", new JsonObject());
        await Settle(casa, trabalho);
        Assert.NotNull(trabalho.Profile.Get(Collections.Shield, "rule|exemplo.test|rastreador.test"));

        trabalho.Profile.Unreadable.Add(Collections.Shield);
        _now += 1000;
        await Settle(trabalho, casa);
        Assert.NotNull(casa.Profile.Get(Collections.Shield, "rule|exemplo.test|rastreador.test"));

        trabalho.Profile.Unreadable.Clear();
        await Settle(trabalho, casa);
        Assert.NotNull(casa.Profile.Get(Collections.Shield, "rule|exemplo.test|rastreador.test"));
        Assert.Equal(casa.Profile.Fingerprint(), trabalho.Profile.Fingerprint());
    }

    // Um perfil que rebenta a ler, com um erro que não é dos passageiros (um defeito, não a rede).
    private sealed class ThrowingProfile(MemoryProfile inner) : ILocalProfileReader
    {
        public bool Throw { get; set; }

        public LocalRead Read() => Throw ? throw new InvalidCastException("dados inventados") : inner.Read();

        public IReadOnlyList<LocalItem> Collect() => Read().Items;

        public IReadOnlyCollection<LocalChange> Apply(IReadOnlyList<LocalChange> changes) => inner.Apply(changes);
    }

    // Auditoria, SYN-05: uma exceção fora das passageiras a meio de uma ronda deixa o erro à vista e uma linha de registo
    // (sem dados), em vez de a ronda falhar calada para sempre.
    [Fact]
    public async Task UnexpectedFailure_IsShownAndLogged_NotSwallowed()
    {
        var lines = new List<string>();
        var profile = new ThrowingProfile(new MemoryProfile());
        var agent = new SyncAgent(() => _cloud, profile, new MemoryStore(), "Casa", Now, Quick) { Log = lines.Add };
        _cloud.Watch("Casa", agent);
        await agent.StartAsync();
        profile.Throw = true;
        Assert.False(await agent.RunAsync());
        Assert.Equal("A sincronização falhou com um erro inesperado (InvalidCastException). O Éteris tenta outra vez mais tarde.", agent.Status.Error);
        Assert.Single(lines);
        Assert.Contains("InvalidCastException", lines[0]);
        Assert.DoesNotContain("dados inventados", lines[0]);

        // Quem chama cancelou: continua a ser um cancelamento, não uma falha.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync(cancellationToken: cancelled.Token));
    }

    // Auditoria, SYN-09: um estado que não se leu não é «não existe»: nada o substitui por um vazio.
    [Fact]
    public async Task UnreadableState_IsNotOverwrittenByStart()
    {
        var inner = new MemoryStore();
        var casa = NewDevice("Casa", store: inner);
        await casa.Agent.StartAsync();
        Assert.True(await casa.Agent.RunAsync());
        var id = inner.Load()!.Engine!.Device.Id;
        var saves = inner.Saves;

        var failing = new FailingStore(inner) { Fail = true };
        var agent = new SyncAgent(() => _cloud, new MemoryProfile(), failing, "Casa", Now, Quick);
        Assert.True(agent.Status.StateUnreadable);
        Assert.Equal(SyncAgent.StateUnreadableText, agent.Status.Notice);
        await Assert.ThrowsAsync<SyncUserException>(() => agent.StartAsync());
        Assert.Throws<SyncUserException>(() => agent.SetCollections([Collections.Bookmarks], on: false));
        Assert.Throws<SyncUserException>(() => agent.BeginCloudDeletion());
        await Assert.ThrowsAsync<SyncUserException>(() => agent.RenameDeviceAsync("Outro nome"));
        Assert.Equal(saves, inner.Saves);

        failing.Fail = false;
        await agent.StartAsync();
        Assert.Equal(SyncPhase.On, agent.Status.Phase);
        Assert.False(agent.Status.StateUnreadable);
        Assert.Equal(id, inner.Load()!.Engine!.Device.Id);
    }

    private sealed class FailingStore(MemoryStore inner) : ISyncStore
    {
        public bool Fail { get; set; }

        public SavedSync? Load() => Fail ? throw new SyncStateUnreadableException("não se leu") : inner.Load();

        public void Save(SavedSync saved) => inner.Save(saved);

        public void Delete() => inner.Delete();
    }

    // Dois computadores criam a conta ao mesmo tempo (Drive vazio para os dois): o de identificador maior sai da sua,
    // volta a «escolher» e junta-se à outra sem perder nada.
    [Fact]
    public async Task TwoComputersCreateTheAccountAtTheSameTime_OneLeaves_AndNothingIsLost()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://casa.example/", "Casa");
        await casa.Agent.StartAsync();
        var hidden = new FlakyTransport(_cloud) { HideRootOnce = true };
        var trabalho = NewDevice("Trabalho", () => hidden);
        trabalho.Profile.Bookmark("https://trabalho.example/", "Trabalho");
        await trabalho.Agent.StartAsync();
        Assert.True(await casa.Agent.RunAsync());
        Assert.True(await trabalho.Agent.RunAsync());

        var phases = new[] { casa.Agent.Status.Phase, trabalho.Agent.Status.Phase };
        Assert.Single(phases, p => p == SyncPhase.Choose);
        Assert.Single(phases, p => p == SyncPhase.On);
        Assert.Single(_cloud.Paths(), p => p.EndsWith("/keys", StringComparison.Ordinal));
        var (left, stayed) = casa.Agent.Status.Phase == SyncPhase.Choose ? (casa, trabalho) : (trabalho, casa);
        Assert.Contains("ao mesmo tempo", left.Agent.Status.Notice);
        await left.Agent.JoinWithCodeAsync(stayed.Agent.RevealKit()!.Words);
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

    // Em pausa por a autorização ter sido retirada ou ter expirado, procurar pedidos de computadores novos ou reler os
    // aparelhos (abrir o menu, as Definições, voltar à janela) não faz pedido nenhum: cada tentativa batia no Drive e no
    // endpoint de tokens, e nada se resolve sem entrar outra vez (revisão final, A-08).
    [Fact]
    public async Task RevokedAuthorization_LookingForRequestsOrDevices_MakesNoRequests()
    {
        var google = new FakeGoogle();
        var casa = NewDevice("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        google.Revoked.Add(google.RefreshToken);
        google.ExpireAccessToken();
        _now += 3_700_000;
        casa.Profile.Bookmark("https://x.example/", "X");
        Assert.False(await casa.Agent.RunAsync());
        Assert.True(casa.Agent.Status.NeedsSignIn);

        var before = google.Requests.Count;
        await casa.Agent.CheckRequestsAsync();
        Assert.Single(await casa.Agent.RefreshDevicesAsync());
        Assert.Equal(before, google.Requests.Count);
        // No navegador, o momento de procurar nem chega ao agente.
        var service = GoogleSyncTests.Src("Eteris.Browser", "Sync", "SyncService.cs");
        Assert.Contains("NeedsSignIn", GoogleSyncTests.Body(service, "public static void Look(bool devices = false)"));
        Assert.Contains("NeedsSignIn", GoogleSyncTests.Body(service, "public static void WindowActivated(bool privateWindow)"));

        // Entrou outra vez: volta a procurar.
        google.Revoked.Clear();
        await casa.Agent.StartAsync();
        Assert.False(casa.Agent.Status.NeedsSignIn);
        var after = google.Requests.Count;
        await casa.Agent.CheckRequestsAsync();
        Assert.True(google.Requests.Count > after);
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

    // «Sair» com «apagar a chave» esquece o kit; sem apagar a chave, o estado do kit fica como estava.
    [Fact]
    public async Task SignOut_ForgettingTheKey_ForgetsTheKit_AndKeepingIt_KeepsTheKitState()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        Assert.Equal(KitState.Pending, casa.Agent.Status.Kit!.State);
        casa.Agent.SignOut(forgetKey: false);
        await casa.Agent.StartAsync();
        Assert.Equal(KitState.Pending, casa.Agent.Status.Kit!.State); // por guardar antes → por guardar depois

        casa.Agent.MarkKitSaved();
        var id = casa.Agent.Status.Kit!.Id;
        casa.Agent.SignOut(forgetKey: false);
        await casa.Agent.StartAsync();
        Assert.Equal(KitState.Saved, casa.Agent.Status.Kit!.State);
        Assert.Equal(id, casa.Agent.Status.Kit.Id);

        casa.Agent.SignOut(forgetKey: true);
        Assert.Null(casa.Agent.Status.Kit);
        Assert.Null(casa.Agent.RevealKit());
    }

    // «Gerar um código de recuperação novo»: o antigo deixa de servir, ninguém sai da conta.
    [Fact]
    public async Task NewRecoveryCode_TheOldOneStopsWorking_EveryoneStays()
    {
        var (casa, trabalho) = await TwoAsync();
        var old = casa.Agent.RevealKit()!.Words;
        casa.Agent.MarkKitSaved();
        await casa.Agent.RefreshDevicesAsync();
        await casa.Agent.NewRecoveryCodeAsync();
        Assert.NotEqual(KitState.Saved, casa.Agent.Status.Kit!.State);
        var fresh = casa.Agent.RevealKit()?.Words;
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

    // O kit de recuperação em 24 palavras abre a conta pelo mesmo caminho do código antigo; as palavras de outra conta
    // e um kit incompleto dão a mensagem certa.
    [Fact]
    public async Task Join_AcceptsTheWords()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var palavras = casa.Agent.RevealKit()!.Words;
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(palavras);
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);

        var outro = NewDevice("Outro");
        await outro.Agent.StartAsync();
        using var alheia = Eteris.Sync.Crypto.AccountSecret.Create();
        var wrong = await Assert.ThrowsAsync<SyncUserException>(() => outro.Agent.JoinWithCodeAsync(alheia.ToWords()));
        Assert.StartsWith("Este kit não abre", wrong.Message);
        var curto = string.Join(' ', palavras.Split(' ')[..23]);
        var count = await Assert.ThrowsAsync<SyncUserException>(() => outro.Agent.JoinWithCodeAsync(curto));
        Assert.Contains("24 palavras", count.Message);
    }

    // ---- kit de recuperação (0.2.15, T5) ----------------------------------------------------------------------------

    // O estado (que vai para a página em cada atualização) nunca leva o segredo: só a impressão digital do kit.
    [Fact]
    public async Task Status_NeverCarriesTheSecret()
    {
        var (casa, _) = await TwoAsync();
        var kit = casa.Agent.RevealKit()!;
        var words = kit.Words.Split(' ');
        string code;
        using (var account = Eteris.Sync.Crypto.AccountSecret.FromCode(kit.Words)) code = account.ToCode();
        var status = System.Text.Json.JsonSerializer.Serialize(casa.Agent.Status);
        var page = System.Text.Json.JsonSerializer.Serialize(Eteris.Browser.Sync.SyncService.StatusData(casa.Agent.Status, _now));
        foreach (var json in new[] { status, page })
        {
            Assert.DoesNotContain(kit.Words, json, StringComparison.Ordinal);
            Assert.DoesNotContain(words[0] + " " + words[1], json, StringComparison.Ordinal);
            Assert.DoesNotContain(code, json, StringComparison.Ordinal);
        }
        Assert.Contains("\"kit\":{\"id\":\"" + kit.KitId + "\"", page, StringComparison.Ordinal);
    }

    // A impressão digital: 4 caracteres Crockford, a mesma para os dois formatos do kit e para o estado.
    [Fact]
    public async Task KitId_Is4Chars_Stable_AndFollowsTheSecret()
    {
        using (var s = Eteris.Sync.Crypto.AccountSecret.Create())
        {
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}$", s.KitId);
            using var fromCode = Eteris.Sync.Crypto.AccountSecret.FromCode(s.ToCode());
            using var fromWords = Eteris.Sync.Crypto.AccountSecret.FromCode(s.ToWords());
            Assert.Equal(s.KitId, fromCode.KitId);
            Assert.Equal(s.KitId, fromWords.KitId);
        }

        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        for (var round = 0; round < 2; round++)
        {
            var kit = casa.Agent.RevealKit()!;
            using var account = Eteris.Sync.Crypto.AccountSecret.FromCode(kit.Words);
            Assert.Equal(kit.KitId, casa.Agent.Status.Kit!.Id);
            Assert.Equal(account.KitId, kit.KitId);
            // Não se afirma que muda: 20 bits; o que muda sempre são as palavras.
            if (round == 0) await casa.Agent.NewRecoveryCodeAsync();
        }
    }

    // Cadeia nova: o kit guardado deixou de valer, aqui e no computador que recebe a conta nova.
    [Fact]
    public async Task Rotation_MakesTheSavedKitStale_HereAndOnTheOtherDevice()
    {
        var (casa, trabalho) = await TwoAsync();
        casa.Agent.MarkKitSaved();
        Assert.Equal(KitState.Saved, trabalho.Agent.Status.Kit!.State); // entrou com o kit: tem-no
        var old = casa.Agent.RevealKit()!.Words;
        await casa.Agent.RefreshDevicesAsync();
        _now += 60_000;
        await casa.Agent.NewRecoveryCodeAsync();
        Assert.True(await trabalho.Agent.RunAsync());

        var fresh = casa.Agent.RevealKit()!.Words;
        Assert.NotEqual(old, fresh);
        Assert.Equal(fresh, trabalho.Agent.RevealKit()!.Words);
        using var account = Eteris.Sync.Crypto.AccountSecret.FromCode(fresh);
        foreach (var device in new[] { casa, trabalho })
        {
            var kit = device.Agent.Status.Kit!;
            Assert.Equal(account.KitId, kit.Id);
            Assert.Equal(KitState.Stale, kit.State);
            Assert.Equal(_now, kit.SinceMs);
        }

        var outro = NewDevice("Outro");
        await outro.Agent.StartAsync();
        await Assert.ThrowsAsync<SyncUserException>(() => outro.Agent.JoinWithCodeAsync(old));
        await outro.Agent.JoinWithCodeAsync(fresh);
        Assert.Equal(SyncPhase.On, outro.Agent.Status.Phase);
    }

    // Uma cadeia nova repõe o «Mais tarde»; um «Não quero» fica só se o kit nunca foi guardado.
    [Fact]
    public async Task Rotation_ResetsTheSnooze_AndKeepsADeclineOnlyIfNeverSaved()
    {
        var a = NewDevice("A");
        await a.Agent.StartAsync();
        a.Agent.SnoozeKit(_now + 1000);
        Assert.Equal(_now + 1000, a.Agent.Status.Kit!.SnoozedUntilMs);
        await a.Agent.NewRecoveryCodeAsync();
        Assert.Equal(0, a.Agent.Status.Kit!.SnoozedUntilMs);

        a.Agent.DeclineKit();
        await a.Agent.NewRecoveryCodeAsync();
        Assert.True(a.Agent.Status.Kit!.Declined);
        Assert.Equal(KitState.Pending, a.Agent.Status.Kit.State);

        a.Agent.MarkKitSaved();
        a.Agent.DeclineKit();
        await a.Agent.NewRecoveryCodeAsync();
        Assert.False(a.Agent.Status.Kit!.Declined);
        Assert.Equal(KitState.Stale, a.Agent.Status.Kit.State);
    }

    // «Não quero» e «Mais tarde» ficam gravados neste computador; sem chave não fazem nada.
    [Fact]
    public async Task Decline_AndSnooze_StickAcrossRestarts()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        casa.Agent.DeclineKit();
        Assert.True(NewDevice("Casa", store: casa.Store, profile: casa.Profile).Agent.Status.Kit!.Declined);
        casa.Agent.SnoozeKit(_now + 12_345);
        Assert.Equal(_now + 12_345, NewDevice("Casa", store: casa.Store, profile: casa.Profile).Agent.Status.Kit!.SnoozedUntilMs);

        var semChave = NewDevice("Novo");
        semChave.Agent.MarkKitSaved();
        semChave.Agent.SnoozeKit(_now + 1);
        semChave.Agent.DeclineKit();
        Assert.Null(semChave.Agent.Status.Kit);
        Assert.Null(semChave.Agent.RevealKit());
    }

    // «Já guardei o kit» diz de que kit se trata: outro kit (cadeia nova entretanto) não fica marcado como guardado.
    [Fact]
    public async Task MarkKitSaved_ForAnotherKit_MarksNothing()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var shown = casa.Agent.Status.Kit!.Id;
        casa.Agent.MarkKitSaved();
        _now += 1000;
        await casa.Agent.NewRecoveryCodeAsync();
        var fresh = casa.Agent.Status.Kit!;
        Assert.Equal(KitState.Stale, fresh.State);
        var saves = casa.Store.Saves;

        casa.Agent.MarkKitSaved(shown == fresh.Id ? "outro" : shown);
        Assert.Equal(KitState.Stale, casa.Agent.Status.Kit!.State);
        Assert.Equal(saves, casa.Store.Saves);

        casa.Agent.MarkKitSaved(fresh.Id);
        Assert.Equal(KitState.Saved, casa.Agent.Status.Kit!.State);
    }

    // Um segredo guardado ilegível não impede o agente de nascer (sem kit, como sem chave).
    [Fact]
    public void CorruptSecret_DoesNotBreakTheAgent()
    {
        foreach (var secret in new[] { "!!!não é base64!!!", "AAAA" })
        {
            var store = new MemoryStore();
            store.Save(new SavedSync { Enabled = true, Secret = secret });
            var agent = NewDevice("Casa", store: store).Agent;
            Assert.Null(agent.Status.Kit);
        }
    }

    // Quem entra com o kit (palavras ou o código antigo) tem-no; quem entra por aprovação fica «por guardar».
    [Fact]
    public async Task JoinWithTheKit_CountsAsSaved_AndJoinByApproval_IsPending()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var words = casa.Agent.RevealKit()!.Words;
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(words);
        Assert.Equal(KitState.Saved, trabalho.Agent.Status.Kit!.State);
        Assert.Equal(KitState.Pending, casa.Agent.Status.Kit!.State); // guardar é local a cada computador

        string code;
        using (var account = Eteris.Sync.Crypto.AccountSecret.FromCode(words)) code = account.ToCode();
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(code);
        Assert.Equal(KitState.Saved, portatil.Agent.Status.Kit!.State);

        var novo = NewDevice("Novo");
        await novo.Agent.StartAsync();
        novo.Agent.RequestPairing();
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && novo.Agent.Status.Pairing?.Code is not null, "o código");
        novo.Agent.ConfirmPairing();
        Assert.Equal(Eteris.Sync.Pairing.PairingAnswer.Approved,
            await casa.Agent.ApproveWithCodeAsync(casa.Agent.Status.Requests[0].Session, novo.Agent.Status.Pairing!.Code!));
        await Until(() => novo.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o novo ligado");
        Assert.Equal(KitState.Pending, novo.Agent.Status.Kit!.State);
    }

    // Um estado da 0.2.14 (sem registo do kit): «código por guardar» passa a kit por guardar; senão, guardado.
    [Fact]
    public async Task Migration_From0214()
    {
        foreach (var pending in new[] { true, false })
        {
            // Cada volta com o seu transporte (duas contas que não se veem).
            var folder = new MemoryTransport();
            var casa = NewDevice("Casa", () => folder);
            await casa.Agent.StartAsync();
            var old = casa.Store.Load()!;
            old.Kit = null;
            old.CodePending = pending;
            casa.Store.Save(old);
            _now += 1000;

            var reopened = NewDevice("Casa", () => folder, casa.Store, casa.Profile);
            var kit = reopened.Agent.Status.Kit!;
            Assert.Equal(pending ? KitState.Pending : KitState.Saved, kit.State);
            Assert.Equal(_now, kit.SinceMs);
            Assert.True(await reopened.Agent.RunAsync());
            var saved = casa.Store.Load()!;
            Assert.False(saved.CodePending);
            Assert.NotNull(saved.Kit);
            Assert.Equal(kit.Id, saved.Kit!.ForId);
        }
    }

    // Quantos computadores há na conta, sem rede: a lista da última ronda, ou as chaves fixadas (mais este).
    [Fact]
    public async Task KnownDevices_Is1Alone_And2AfterASecondJoins()
    {
        var alone = new MemoryTransport();
        var sozinho = NewDevice("Sozinho", () => alone);
        await sozinho.Agent.StartAsync();
        Assert.Equal(1, sozinho.Agent.Status.Kit!.KnownDevices);

        // Dois computadores com dados (como um perfil real: a Casa conhece o Trabalho pelos lotes que ele envia).
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://casa.example/", "Casa");
        await casa.Agent.StartAsync();
        var trabalho = NewDevice("Trabalho");
        trabalho.Profile.Bookmark("https://trabalho.example/", "Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        await Settle(casa, trabalho);
        Assert.Equal(2, casa.Agent.Status.Kit!.KnownDevices);
        Assert.Equal(2, trabalho.Agent.Status.Kit!.KnownDevices);
        // Reabrir a Casa, antes de qualquer ronda: contado pelas chaves fixadas.
        Assert.Equal(2, NewDevice("Casa", store: casa.Store, profile: casa.Profile).Agent.Status.Kit!.KnownDevices);

        var devices = await casa.Agent.RefreshDevicesAsync();
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => !d.IsThis).Id);
        Assert.Equal(1, casa.Agent.Status.Kit!.KnownDevices);
    }

    // A lista dos aparelhos foi lida com um só computador; um segundo entra na mesma sessão: a contagem passa a 2.
    [Fact]
    public async Task KnownDevices_CountsASecondThatJoinsAfterTheListWasRead()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://casa.example/", "Casa");
        await casa.Agent.StartAsync();
        Assert.Single(await casa.Agent.RefreshDevicesAsync());
        Assert.Equal(1, casa.Agent.Status.Kit!.KnownDevices);

        // Entra por aprovação, já com a lista da Casa lida: a ronda seguinte da Casa vê-o e fixa-lhe a chave.
        var trabalho = NewDevice("Trabalho");
        trabalho.Profile.Bookmark("https://trabalho.example/", "Trabalho");
        await trabalho.Agent.StartAsync();
        trabalho.Agent.RequestPairing();
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && trabalho.Agent.Status.Pairing?.Code is not null, "o código");
        trabalho.Agent.ConfirmPairing();
        Assert.Equal(Eteris.Sync.Pairing.PairingAnswer.Approved,
            await casa.Agent.ApproveWithCodeAsync(casa.Agent.Status.Requests[0].Session, trabalho.Agent.Status.Pairing!.Code!));
        await Until(() => trabalho.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o Trabalho ligado");
        await Settle(casa, trabalho);
        Assert.Equal(2, casa.Agent.Status.Kit!.KnownDevices);
    }

    // A página de fim da entrada lê a fase logo que StartAsync devolve a tarefa: tem de ler já «Starting», nunca a fase
    // da ronda anterior (um «On» antigo dava «Pronto.» antes de se saber como ficou).
    [Fact]
    public async Task StartAsync_IsAlreadyStarting_BeforeItsFirstAwait()
    {
        var gated = new GatedTransport(_cloud);
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
        // Cada escrita e cada apagamento no Drive falso acorda as esperas do teste.
        return () => _cloud.Signalling(new GoogleDriveTransport(http, session.AccessTokenAsync, (_, _) => Task.CompletedTask));
    }

    private async Task<(Device Casa, Device Trabalho)> TwoAsync()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()!.Words);
        await Settle(casa, trabalho);
        return (casa, trabalho);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }
}
