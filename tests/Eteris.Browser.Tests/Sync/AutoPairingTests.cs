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

    // As esperas do agente (sondagens do pedido): curtas, sem relógio real. Contadas: depois de uma espera, a sondagem
    // em curso já leu a hora de início (o relógio dos testes só avança depois disso).
    private int _waits;

    private Task Quick(TimeSpan _, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waits);
        return Task.Delay(5, cancellationToken);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store);

    private Device Pc(string name, Func<ISyncTransport> transport, MemoryStore? store = null, MemoryProfile? profile = null, bool autoPair = false,
        Func<bool>? visible = null)
    {
        profile ??= new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport, profile, store, name, Now, Quick)
        {
            PairingPoll = TimeSpan.FromMilliseconds(5),
            AutoPair = autoPair,
            PairingScreenVisible = visible,
        };
        return new Device(agent, profile, store);
    }

    // Os prazos comparam com «>»: 10 minutos e 1 segundo passam o prazo de uma sessão.
    private static readonly long Session = (long)TimeSpan.FromMinutes(10).TotalMilliseconds + 1_000;

    // Espera que a sondagem da sessão atual já tenha corrido (e lido o seu prazo) e só então avança o relógio.
    private async Task AdvanceAsync(long ms)
    {
        var before = Volatile.Read(ref _waits);
        await Until(() => Volatile.Read(ref _waits) > before + 1, "a sondagem a correr");
        Interlocked.Add(ref _now, ms);
    }

    private static int Refusals(FakeGoogle google) => google.Files.Count(f => f.Name.EndsWith("/x-host", StringComparison.Ordinal));

    // O computador que já está na conta procura e responde, até o computador novo ter o código.
    private static async Task AnswerUntilCodeAsync(Device host, Device guest)
    {
        for (var i = 0; i < 300 && guest.Agent.Status.Pairing?.Code is null; i++)
        {
            await host.Agent.CheckRequestsAsync();
            await Task.Delay(20);
        }
        Assert.NotNull(guest.Agent.Status.Pairing?.Code);
    }

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
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()?.Words ?? throw new InvalidOperationException("sem código"));
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
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.RevealKit()?.Words ?? throw new InvalidOperationException("sem código"));
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

    // ---- fim do pedido e renovação (b1) -----------------------------------------------------------------------------

    // O computador novo arranca o pedido automático sobre a conta da «casa».
    private async Task<(FakeGoogle Google, Device Casa, Device Novo)> AskingAsync(Func<bool>? visible = null)
    {
        var google = new FakeGoogle();
        var casa = await HomeAsync(google);
        var novo = Pc("Novo", DriveFor(google), autoPair: true, visible: visible);
        await novo.Agent.StartAsync(false);
        await Until(() => Requests(google) == 1, "o pedido no Drive");
        return (google, casa, novo);
    }

    [Fact]
    public async Task NoCode_BeforeTheHostAnswers()
    {
        var (_, casa, novo) = await AskingAsync();
        await AdvanceAsync(0);
        var pairing = novo.Agent.Status.Pairing!;
        Assert.Null(pairing.Code);
        Assert.False(pairing.HostAnswered);
        Assert.Equal(GuestEnd.None, pairing.End);
        await AnswerUntilCodeAsync(casa, novo);
        Assert.True(novo.Agent.Status.Pairing!.HostAnswered);
        Assert.Matches(@"^\d{3} \d{3}$", novo.Agent.Status.Pairing!.Code!);
        novo.Agent.CancelPairing();
    }

    [Fact]
    public async Task NobodyAnswers_ScreenVisible_Renews()
    {
        var (google, _, novo) = await AskingAsync(visible: () => true);
        await AdvanceAsync(Session);
        await Until(() => Requests(google) == 2, "a segunda sessão");
        await Until(() => Refusals(google) == 1, "a sessão velha recusada");
        var status = novo.Agent.Status;
        Assert.Equal(SyncPhase.Pairing, status.Phase);
        Assert.Equal(GuestEnd.None, status.Pairing!.End);
        Assert.Null(status.Pairing.Code);
        Assert.Null(status.Pairing.Error);
        novo.Agent.CancelPairing();
    }

    [Fact]
    public async Task NobodyAnswers_ScreenHidden_StopsAndAsks()
    {
        var (google, _, novo) = await AskingAsync(visible: () => false);
        await AdvanceAsync(Session);
        await Until(() => novo.Agent.Status.Pairing?.End == GuestEnd.StillWaiting, "o fim do pedido");
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.False(novo.Agent.Status.Pairing!.HostAnswered);
        Assert.Equal(1, Requests(google));
        // Sem PairingScreenVisible (null) também nunca renova.
        var (google2, _, outro) = await AskingAsync();
        await AdvanceAsync(Session);
        await Until(() => outro.Agent.Status.Pairing?.End == GuestEnd.StillWaiting, "o fim do pedido sem ecrã");
        Assert.Equal(1, Requests(google2));
    }

    [Fact]
    public async Task Renewal_StopsAfterThirtyMinutes()
    {
        var (google, _, novo) = await AskingAsync(visible: () => true);
        await AdvanceAsync(Session);
        await Until(() => Requests(google) == 2, "a segunda sessão");
        // A segunda sessão acaba já depois da meia hora desde o primeiro pedido: não renova outra vez.
        await AdvanceAsync((long)TimeSpan.FromMinutes(25).TotalMilliseconds);
        await Until(() => novo.Agent.Status.Pairing?.End == GuestEnd.StillWaiting, "o fim da renovação");
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.Equal(2, Requests(google));
    }

    [Fact]
    public async Task HostAnswered_ThenTheTimeRunsOut_IsTimeUp_AndIsNotRenewed()
    {
        var (google, casa, novo) = await AskingAsync(visible: () => true);
        await AnswerUntilCodeAsync(casa, novo);
        await AdvanceAsync(Session);
        await Until(() => novo.Agent.Status.Pairing?.End == GuestEnd.TimeUp, "o tempo acabou");
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.True(novo.Agent.Status.Pairing!.HostAnswered);
        Assert.Equal(1, Requests(google));
        // O ciclo acabou: os pedidos ao Drive param de crescer.
        await Task.Delay(300);
        var count = google.Requests.Count;
        await Task.Delay(300);
        Assert.Equal(count, google.Requests.Count);
    }

    [Fact]
    public async Task Refused_IsNotRenewed()
    {
        var (google, casa, novo) = await AskingAsync(visible: () => true);
        await AnswerUntilCodeAsync(casa, novo);
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o pedido no anfitrião");
        await casa.Agent.RefuseAsync(casa.Agent.Status.Requests[0].Session);
        await Until(() => novo.Agent.Status.Pairing?.End == GuestEnd.Refused, "a recusa");
        Assert.Contains("recusou", novo.Agent.Status.Pairing!.Error);
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        await Task.Delay(50);
        Assert.Equal(1, Requests(google));
    }

    [Fact]
    public async Task AutoRequest_ThenTheOldComputerTypesTheCode_Joins()
    {
        var (google, casa, novo) = await AskingAsync();
        await AnswerUntilCodeAsync(casa, novo);
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o pedido no anfitrião");
        var session = casa.Agent.Status.Requests[0].Session;
        // Nenhum ficheiro do pedido leva o nome do computador novo em claro.
        foreach (var (_, data) in google.Files.Where(f => f.Name.Contains("/pair/", StringComparison.Ordinal)))
            Assert.DoesNotContain("Novo", System.Text.Encoding.UTF8.GetString(data), StringComparison.Ordinal);
        novo.Agent.ConfirmPairing();
        Assert.Equal(Eteris.Sync.Pairing.PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, novo.Agent.Status.Pairing!.Code!));
        await Until(() => novo.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o computador novo ligado");
        Assert.Contains("Casa", novo.Agent.Status.Notice);
    }

    [Fact]
    public async Task Joined_SaysG4_UntilTheFirstRoundEnds()
    {
        // Por aprovação.
        var (_, casa, novo) = await AskingAsync();
        var seen = new List<bool>();
        novo.Agent.Changed += () => { lock (seen) seen.Add(novo.Agent.Status.JoinedNow); };
        await AnswerUntilCodeAsync(casa, novo);
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o pedido no anfitrião");
        novo.Agent.ConfirmPairing();
        await casa.Agent.ApproveWithCodeAsync(casa.Agent.Status.Requests[0].Session, novo.Agent.Status.Pairing!.Code!);
        await Until(() => novo.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o computador novo ligado");
        lock (seen) Assert.Contains(true, seen);
        Assert.False(novo.Agent.Status.JoinedNow);

        // Pelo kit (o código de recuperação).
        var google = new FakeGoogle();
        var home = await HomeAsync(google);
        var kit = Pc("Kit", DriveFor(google));
        await kit.Agent.StartAsync();
        var kitSeen = new List<bool>();
        kit.Agent.Changed += () => { lock (kitSeen) kitSeen.Add(kit.Agent.Status.JoinedNow); };
        await kit.Agent.JoinWithCodeAsync(home.Agent.RevealKit()!.Words);
        lock (kitSeen) Assert.Contains(true, kitSeen);
        Assert.False(kit.Agent.Status.JoinedNow);

        // Voltar a entrar com a chave guardada não é entrar de novo.
        var saved = kit.Store.Load()!;
        saved.Engine = null;
        kit.Store.Save(saved);
        var again = Pc("Kit", DriveFor(google), kit.Store, kit.Profile);
        var againSeen = new List<bool>();
        again.Agent.Changed += () => { lock (againSeen) againSeen.Add(again.Agent.Status.JoinedNow); };
        await again.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, again.Agent.Status.Phase);
        lock (againSeen) Assert.DoesNotContain(true, againSeen);
    }

    [Fact]
    public async Task OneAutoRequestPerGesture_AndAtMostThreeSessions()
    {
        var (google, _, novo) = await AskingAsync(visible: () => true);
        for (var sessions = 1; sessions <= 3; sessions++)
        {
            await Until(() => Requests(google) == sessions, $"a sessão {sessions}");
            await AdvanceAsync(Session);
        }
        await Until(() => novo.Agent.Status.Pairing?.End == GuestEnd.StillWaiting, "o fim das renovações");
        Assert.Equal(3, Requests(google));
        // Mais tempo e uma ronda do agendador: nenhum pedido novo.
        Interlocked.Add(ref _now, Session * 3);
        Assert.True(await novo.Agent.RunAsync());
        await Task.Delay(50);
        Assert.Equal(3, Requests(google));
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
    }

    // As falhas distinguem-se pela propriedade Failure, sem tipos de exceção novos (SyncPairingTests afirma o tipo exato).
    [Fact]
    public async Task Failures_AreTyped_WithoutNewExceptionTypes()
    {
        var transport = new FolderTransport(_folder);
        var clock = 0L;
        Func<long> now = () => clock += 6 * 60_000;
        var session = await Eteris.Sync.Pairing.PairingRequests.OpenAsync(transport, () => 0);
        var timeout = await Assert.ThrowsAsync<Eteris.Sync.Pairing.PairingException>(() =>
            Eteris.Sync.Pairing.PairingRequests.WaitForHostAsync(transport, session, "Novo", now, (_, _) => Task.CompletedTask));
        Assert.Equal(typeof(Eteris.Sync.Pairing.PairingException), timeout.GetType());
        Assert.Equal(Eteris.Sync.Pairing.PairingFailure.TimedOut, timeout.Failure);
        Assert.Equal("Nenhum aparelho respondeu em 10 minutos. Comece de novo.", timeout.Message);

        await Eteris.Sync.Pairing.PairingRequests.RefuseAsync(transport, session, () => 0);
        var refused = await Assert.ThrowsAsync<Eteris.Sync.Pairing.PairingException>(() =>
            Eteris.Sync.Pairing.PairingRequests.WaitForHostAsync(transport, session, "Novo", () => 0, (_, _) => Task.CompletedTask));
        Assert.Equal(Eteris.Sync.Pairing.PairingFailure.Refused, refused.Failure);
        Assert.Equal("O outro aparelho recusou o pedido.", refused.Message);

        Assert.Equal(Eteris.Sync.Pairing.PairingFailure.Other, new Eteris.Sync.Pairing.PairingException("Mensagem do convidado incompleta.").Failure);
        Assert.Equal(Eteris.Sync.Pairing.PairingFailure.Other, new Eteris.Sync.Pairing.PairingAnsweredElsewhereException().Failure);
        var names = typeof(Eteris.Sync.Pairing.PairingException).Assembly.GetTypes().Select(t => t.Name).ToList();
        Assert.DoesNotContain("PairingTimeoutException", names);
        Assert.DoesNotContain("PairingRefusedException", names);
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
        // O fim do pedido e se alguém respondeu (b1).
        var ended = TestStatus.Of(SyncPhase.Choose, pairing: new GuestPairing(null, false, "O emparelhamento expirou. Comece de novo.")
            { HostAnswered = true, End = GuestEnd.TimeUp });
        data = Eteris.Browser.Sync.SyncService.StatusData(ended, _now).ToString();
        Assert.Contains("hostAnswered = True", data, StringComparison.Ordinal);
        Assert.Contains("end = timeUp", data, StringComparison.Ordinal);
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
