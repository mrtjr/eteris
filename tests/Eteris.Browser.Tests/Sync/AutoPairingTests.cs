using System.IO;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Google;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// O pedido de aprovação automático no computador novo (0.2.15, T7): só um computador sem nenhum estado desta conta
// pede sozinho, e só na ronda do gesto de entrar ou de ligar. Um computador cuja conta desapareceu do Drive (e lá está
// outra) não pede nada: mostra o alerta «conta substituída».
public sealed class AutoPairingTests : IDisposable
{
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-auto-pairing-" + Guid.NewGuid().ToString("N"));

    private long Now() => _now;

    // As esperas do agente (sondagens do pedido): curtas, sem relógio real.
    private static Task Quick(TimeSpan _, CancellationToken cancellationToken) => Task.Delay(5, cancellationToken);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store);

    private static Device NewDevice(string name, Func<ISyncTransport> transport, Func<long> now, MemoryStore? store = null, MemoryProfile? profile = null,
        bool autoPair = false)
    {
        profile ??= new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport, profile, store, name, now, Quick)
        {
            PairingPoll = TimeSpan.FromMilliseconds(5),
            AutoPair = autoPair,
        };
        return new Device(agent, profile, store);
    }

    private Device Pc(string name, Func<ISyncTransport> transport, MemoryStore? store = null, MemoryProfile? profile = null, bool autoPair = false)
        => NewDevice(name, transport, Now, store, profile, autoPair);

    private Func<ISyncTransport> DriveFor(FakeGoogle google)
    {
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var session = new GoogleSession(oauth, new TokenStore(Path.Combine(_folder, "tokens-" + Guid.NewGuid().ToString("N")), new PlainProtector()), Now);
        session.Use(new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope));
        return () => new GoogleDriveTransport(http, session.AccessTokenAsync, (_, _) => Task.CompletedTask);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 3000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "À espera de: " + what);
    }

    private static int Requests(FakeGoogle google) => google.Files.Count(f => f.Name.EndsWith("/0-request", StringComparison.Ordinal));

    private static bool AnyPairing(FakeGoogle google) => google.Files.Any(f => f.Name.Contains("/pair/", StringComparison.Ordinal));

    // A «casa» cria a conta no Drive falso.
    private async Task<Device> HomeAsync(FakeGoogle google)
    {
        var casa = Pc("Casa", DriveFor(google));
        await casa.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
        return casa;
    }

    // ---- quem pode pedir sozinho (a1) -------------------------------------------------------------------------------

    [Fact]
    public void MayAutoRequest_OnlyWithoutAnyState()
    {
        Assert.True(SyncAgent.MayAutoRequest(null));
        Assert.True(SyncAgent.MayAutoRequest(new SavedSync { Enabled = true }));
        Assert.False(SyncAgent.MayAutoRequest(new SavedSync { Secret = "abc" }));
        Assert.False(SyncAgent.MayAutoRequest(new SavedSync { Account = "impressao" }));
        Assert.False(SyncAgent.MayAutoRequest(new SavedSync { HadAccount = true }));
        // A marca é local e sobrevive ao reabrir (ida e volta pelo JSON do estado guardado).
        Assert.True(Json.Parse<SavedSync>(Json.Bytes(new SavedSync { HadAccount = true })).HadAccount);
    }

    [Fact]
    public async Task SignIn_OnADriveWithAnAccount_AndNoKey_AsksByItself()
    {
        var google = new FakeGoogle();
        await HomeAsync(google);
        var novo = Pc("Novo", DriveFor(google), autoPair: true);
        await novo.Agent.StartAsync(false);
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);
        await Until(() => Requests(google) == 1, "o pedido no Drive");
        Assert.False(novo.Agent.Status.ChainReplaced);
        await Task.Delay(50);
        Assert.Equal(1, Requests(google));
        novo.Agent.CancelPairing();
    }

    [Fact]
    public async Task AutoPairOff_StaysInChoose()
    {
        var google = new FakeGoogle();
        await HomeAsync(google);
        var novo = Pc("Novo", DriveFor(google));
        Assert.False(novo.Agent.AutoPair);
        await novo.Agent.StartAsync(false);
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.True(novo.Agent.Status.MayAutoPair);
        Assert.False(novo.Agent.Status.ChainReplaced);
        await Task.Delay(50);
        Assert.False(AnyPairing(google));
    }

    [Fact]
    public async Task SavedKey_WhoseChainExists_JoinsWithoutAsking()
    {
        var google = new FakeGoogle();
        var casa = await HomeAsync(google);
        var trabalho = Pc("Trabalho", DriveFor(google));
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode ?? throw new InvalidOperationException("sem código"));
        Assert.Equal(SyncPhase.On, trabalho.Agent.Status.Phase);
        // Entrou: este computador já teve chave desta conta.
        Assert.False(SyncAgent.MayAutoRequest(trabalho.Store.Load()));

        // «Sair» sem apagar a chave e reabrir o Éteris com o automático: volta a entrar sem pedir nada.
        trabalho.Agent.SignOut(forgetKey: false);
        var reaberto = Pc("Trabalho", DriveFor(google), trabalho.Store, trabalho.Profile, autoPair: true);
        await reaberto.Agent.StartAsync(false);
        Assert.Equal(SyncPhase.On, reaberto.Agent.Status.Phase);
        Assert.False(reaberto.Agent.Status.ChainReplaced);
        Assert.False(reaberto.Agent.Status.MayAutoPair);
        await Task.Delay(50);
        Assert.False(AnyPairing(google));
    }

    [Fact]
    public async Task PhaseOff_SendsNothing()
    {
        var google = new FakeGoogle();
        var novo = Pc("Novo", DriveFor(google), autoPair: true);
        Assert.Equal(SyncPhase.Off, novo.Agent.Status.Phase);
        Assert.True(await novo.Agent.RunAsync());
        Assert.True(await novo.Agent.RunAsync(force: true));
        Assert.Equal(SyncPhase.Off, novo.Agent.Status.Phase);
        Assert.Empty(google.Requests);
    }

    [Fact]
    public async Task ScheduledRound_ThatReachesChoose_DoesNotAsk()
    {
        var google = new FakeGoogle();
        await HomeAsync(google);
        var novo = Pc("Novo", DriveFor(google), autoPair: true);
        await novo.Agent.StartAsync(false);
        await Until(() => Requests(google) == 1, "o pedido do gesto");
        novo.Agent.CancelPairing();
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);

        // Reabrir o Éteris: as rondas do agendador (e as forçadas) não são o gesto de entrar.
        var reaberto = Pc("Novo", DriveFor(google), novo.Store, novo.Profile, autoPair: true);
        Assert.True(await reaberto.Agent.RunAsync());
        Assert.Equal(SyncPhase.Choose, reaberto.Agent.Status.Phase);
        Assert.True(await reaberto.Agent.RunAsync(force: true));
        Assert.Equal(SyncPhase.Choose, reaberto.Agent.Status.Phase);
        Assert.True(reaberto.Agent.Status.MayAutoPair);
        Assert.False(reaberto.Agent.Status.ChainReplaced);
        await Task.Delay(50);
        Assert.Equal(1, Requests(google));
    }

    // B estava ligado à conta de A; a conta foi apagada e C criou outra no mesmo Drive.
    private async Task<(FakeGoogle Google, Device Replaced)> ReplacedAsync()
    {
        var google = new FakeGoogle();
        var casa = await HomeAsync(google);
        var trabalho = Pc("Trabalho", DriveFor(google));
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode ?? throw new InvalidOperationException("sem código"));
        Assert.True(await casa.Agent.RunAsync());
        Assert.True(await trabalho.Agent.RunAsync());
        await casa.Agent.DeleteCloudDataAsync();
        var outro = Pc("Outro", DriveFor(google));
        await outro.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, outro.Agent.Status.Phase);

        // O Éteris do trabalho reabre (já com o automático) e faz a ronda do agendador.
        var b = Pc("Trabalho", DriveFor(google), trabalho.Store, trabalho.Profile, autoPair: true);
        Assert.True(await b.Agent.RunAsync());
        return (google, b);
    }

    [Fact]
    public async Task ChainGone_AndAnotherChainInTheDrive_AsksNothing_AndShowsTheAlert()
    {
        var (google, b) = await ReplacedAsync();
        Assert.Equal(SyncPhase.Stopped, b.Agent.Status.Phase);
        Assert.True(b.Agent.Status.ChainReplaced);
        Assert.False(b.Agent.Status.MayAutoPair);

        // «Ligar outra vez» (o gesto): o Drive tem outra conta; este computador tem a chave da antiga.
        await b.Agent.StartAsync(false);
        Assert.Equal(SyncPhase.Choose, b.Agent.Status.Phase);
        Assert.True(b.Agent.Status.ChainReplaced);
        Assert.False(b.Agent.Status.MayAutoPair);
        await Task.Delay(50);
        Assert.Equal(0, Requests(google));
    }

    [Fact]
    public async Task Replaced_FoiEu_AsksExplicitly()
    {
        var (google, b) = await ReplacedAsync();
        await b.Agent.StartAsync(false);
        Assert.Equal(0, Requests(google));
        // O gesto explícito continua a funcionar no agente (a confirmação do Windows é do serviço).
        b.Agent.RequestPairing();
        Assert.Equal(SyncPhase.Pairing, b.Agent.Status.Phase);
        Assert.False(b.Agent.Status.ChainReplaced);
        await Until(() => Requests(google) == 1, "o pedido explícito");
        b.Agent.CancelPairing();
    }

    [Fact]
    public async Task LostTheRace_DoesNotAskByItself()
    {
        Func<ISyncTransport> folder = () => new FolderTransport(_folder);
        var casa = Pc("Casa", folder);
        await casa.Agent.StartAsync();
        var hidden = new HideRootOnceTransport(new FolderTransport(_folder));
        var trabalho = Pc("Trabalho", () => hidden, autoPair: true);
        await trabalho.Agent.StartAsync(false);
        Assert.True(await casa.Agent.RunAsync());
        Assert.True(await trabalho.Agent.RunAsync());

        var (left, _) = casa.Agent.Status.Phase == SyncPhase.Choose ? (casa, trabalho) : (trabalho, casa);
        Assert.Equal(SyncPhase.Choose, left.Agent.Status.Phase);
        Assert.Contains("ao mesmo tempo", left.Agent.Status.Notice);
        Assert.False(left.Agent.Status.ChainReplaced);
        Assert.False(left.Agent.Status.MayAutoPair);
        // Perdeu a conta que criou: já teve chave, por isso nunca pede sozinho.
        Assert.False(SyncAgent.MayAutoRequest(left.Store.Load()));
        await Task.Delay(50);
        Assert.False(Directory.Exists(Path.Combine(_folder, "eteris", "pair")));
    }

    [Fact]
    public async Task HadAccount_OrAFingerprint_DoesNotAskByItself()
    {
        foreach (var saved in new[] { new SavedSync { HadAccount = true }, new SavedSync { Account = "x" } })
        {
            var google = new FakeGoogle();
            await HomeAsync(google);
            var store = new MemoryStore();
            store.Save(saved);
            var novo = Pc("Novo", DriveFor(google), store, autoPair: true);
            await novo.Agent.StartAsync(false);
            Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
            Assert.False(novo.Agent.Status.MayAutoPair);
            Assert.False(novo.Agent.Status.ChainReplaced);
            await Task.Delay(50);
            Assert.False(AnyPairing(google));
        }
    }

    [Fact]
    public async Task ForgetKey_MakesTheComputerNewAgain()
    {
        var google = new FakeGoogle();
        var casa = await HomeAsync(google);
        Assert.False(SyncAgent.MayAutoRequest(casa.Store.Load()));
        casa.Agent.SignOut(forgetKey: true);
        Assert.Null(casa.Store.Load());
        Assert.True(SyncAgent.MayAutoRequest(casa.Store.Load()));
    }

    // A página das Definições recebe as duas marcas no estado (só leitura; o agente não é criado).
    [Fact]
    public void StatusData_CarriesTheMarks()
    {
        var data = Eteris.Browser.Sync.SyncService.StatusData(TestStatus.Of(SyncPhase.Choose) with { ChainReplaced = true, MayAutoPair = true }, _now).ToString();
        Assert.Contains("chainReplaced = True", data, StringComparison.Ordinal);
        Assert.Contains("mayAutoPair = True", data, StringComparison.Ordinal);
        data = Eteris.Browser.Sync.SyncService.StatusData(TestStatus.Of(SyncPhase.Choose), _now).ToString();
        Assert.Contains("chainReplaced = False", data, StringComparison.Ordinal);
        Assert.Contains("mayAutoPair = False", data, StringComparison.Ordinal);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    // A primeira listagem da raiz vem vazia: os dois computadores criam a conta «ao mesmo tempo».
    private sealed class HideRootOnceTransport(ISyncTransport inner) : ISyncTransport
    {
        private bool _hide = true;

        public string Name => inner.Name;

        public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            if (_hide && prefix == SyncPaths.Root)
            {
                _hide = false;
                return [];
            }
            return await inner.ListAsync(prefix, cancellationToken);
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);
    }
}
