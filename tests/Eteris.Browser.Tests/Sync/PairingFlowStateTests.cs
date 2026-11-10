using System.IO;
using Eteris.Browser.Sync;
using Eteris.Sync.Engine;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// O estado dos pedidos de aprovação quando alguma coisa corre fora do caminho normal (revisão final da 0.2.15, A-06 e
// A-07): uma entrega que falha de forma inesperada não deixa o pedido preso em «ocupado»; ligar outra vez com um pedido
// vivo não o esconde; a sincronização parada não deixa pedidos a sondar o transporte; e uma primeira ronda que falha a
// seguir à aprovação fica à vista e com nova tentativa marcada. Em memória, com esperas por sinal.
public sealed class PairingFlowStateTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(1, ct);
    private long _now = 1_762_560_000_000;
    private readonly SyncTestCloud _cloud = new();
    private string? _account = "conta-a";
    private readonly List<(string Name, bool Ok)> _rounds = [];

    private long Now() => _now;

    public void Dispose() => _cloud.StopAll();

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store, ScriptedTransport Transport);

    private Device NewDevice(string name)
    {
        var profile = new MemoryProfile();
        var store = new MemoryStore();
        var transport = new ScriptedTransport(_cloud, _cloud.Pulse);
        var agent = new SyncAgent(() => transport, profile, store, name, Now, Quick)
        {
            PairingPoll = TimeSpan.FromMilliseconds(1),
            AccountKey = () => _account,
            RoundEnded = ok =>
            {
                lock (_rounds) _rounds.Add((name, ok));
                _cloud.Pulse();
            },
        };
        _cloud.Watch(name, agent);
        return new Device(agent, profile, store, transport);
    }

    private async Task<(Device Casa, Device Novo)> TwoAsync()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        await casa.Agent.StartAsync();
        casa.Agent.MarkKitSaved();
        var novo = NewDevice("Portátil");
        novo.Profile.Bookmark("https://portatil.example/", "Do portátil");
        await novo.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        return (casa, novo);
    }

    private async Task<(string Session, string Code)> ReadyAsync(Device casa, Device novo)
    {
        if (novo.Agent.Status.Phase != SyncPhase.Pairing) novo.Agent.RequestPairing();
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) >= 1, "o pedido no transporte");
        await casa.Agent.CheckRequestsAsync();
        await _cloud.WaitFor(() => casa.Agent.Status.Requests.Count == 1 && novo.Agent.Status.Pairing?.Code is not null, "o pedido pronto");
        return (casa.Agent.Status.Requests[0].Session, novo.Agent.Status.Pairing!.Code!);
    }

    // ---- A-06 (1): a entrega que falha de forma inesperada ---------------------------------------------------------

    [Fact]
    public async Task UnexpectedFailure_WhileDelivering_DoesNotLeaveTheRequestBusy()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        novo.Agent.ConfirmPairing();
        casa.Transport.FailWrite("/" + PairingProtocol.Sealed, () => new InvalidOperationException("pormenor interno"));
        var failed = await Assert.ThrowsAsync<SyncUserException>(() => casa.Agent.ApproveWithCodeAsync(session, code));
        Assert.DoesNotContain("pormenor interno", failed.Message);
        Assert.Single(casa.Agent.Status.Requests);

        // A tentativa seguinte não dá «ocupado»: entrega.
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await _cloud.WaitFor(() => novo.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
    }

    // ---- A-06 (2): ligar outra vez com um pedido vivo -----------------------------------------------------------------

    [Fact]
    public async Task Start_WithALiveRequest_KeepsItOnScreen()
    {
        var (casa, novo) = await TwoAsync();
        novo.Agent.RequestPairing();
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 1, "o pedido no transporte");

        // «Ligar» outra vez (ou entrar outra vez) a meio do pedido: o pedido continua e continua à vista.
        await novo.Agent.StartAsync();
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);
        Assert.NotNull(novo.Agent.Status.Pairing);
        Assert.Null(novo.Agent.Status.Pairing!.Error);
        // «Pedir outra vez» com o pedido vivo não abre um segundo.
        novo.Agent.RequestPairing();
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);

        var (session, code) = await ReadyAsync(casa, novo);
        Assert.Equal(1, _cloud.PairingFiles(PairingProtocol.Request));
        novo.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await _cloud.WaitFor(() => novo.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
    }

    // Um pedido que acabou com erro já não mexe na fase de um pedido novo.
    [Fact]
    public async Task EndedRequest_DoesNotChangeThePhaseOfANewOne()
    {
        var (_, novo) = await TwoAsync();
        // O 1.º pedido fica preso na escrita do 0-request; entretanto cancela-se e pede-se outra vez.
        var hold = novo.Transport.HoldWrite("/" + PairingProtocol.Request);
        novo.Agent.RequestPairing();
        await hold.Arrived.WaitAsync(TimeSpan.FromMinutes(2));
        novo.Agent.CancelPairing();
        novo.Agent.RequestPairing();
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);
        // O 1.º acaba agora, com uma falha: a fase é a do pedido novo.
        hold.Fail(new InvalidOperationException("pormenor interno"));
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 1, "o pedido novo no transporte");
        await Task.Delay(50);
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);
        Assert.Null(novo.Agent.Status.Pairing!.Error);
    }

    // ---- A-06 (3): a sincronização parada não deixa pedidos vivos ---------------------------------------------------

    [Fact]
    public async Task Stop_CancelsTheRequestsBeingFollowed()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        // Um segundo pedido, respondido e ainda sem código.
        var forged = await PairingRequests.OpenAsync(_cloud, Now);
        await casa.Agent.CheckRequestsAsync();
        Assert.NotEqual(0, casa.Agent.Status.AnsweringSinceMs);

        // A sessão passa a ser de outra conta Google: a ronda seguinte põe a sincronização em pausa.
        _account = "conta-b";
        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, casa.Agent.Status.Phase);
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.Equal(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.Equal(0, casa.Agent.Status.WaitingRequests);
        // Nem o código certo entrega a chave guardada, e ninguém continua a sondar o transporte por estes pedidos.
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(session, code));
        Assert.False(_cloud.Has(SyncPaths.Pairing(session) + PairingProtocol.Sealed));
        await Task.Delay(50);
        var reads = casa.Transport.Reads;
        await Task.Delay(100);
        // Quando muito, a leitura que já ia a meio em cada pedido (quem continuasse a sondar somava dezenas).
        Assert.InRange(casa.Transport.Reads, reads, reads + 2);
        Assert.False(_cloud.Has(SyncPaths.Pairing(forged) + PairingProtocol.Refused));
        Assert.Equal(0, casa.Store.Load()!.BadPairings?.Length ?? 0);
    }

    // O computador novo com o pedido aberto: a pausa também o para.
    [Fact]
    public async Task Stop_CancelsTheRequestOfTheNewComputer()
    {
        var (_, novo) = await TwoAsync();
        // Este aparelho já sincronizou com outra conta Google (a impressão digital guardada é de outra sessão) e está
        // ligado sem chave: um agente novo sobre esse estado guardado.
        novo.Agent.SignOut(forgetKey: false);
        var saved = novo.Store.Load()!;
        saved.Account = "conta-antiga";
        saved.Enabled = true;
        novo.Store.Save(saved);
        var reaberto = new SyncAgent(() => novo.Transport, novo.Profile, novo.Store, "Portátil", Now, Quick)
        {
            PairingPoll = TimeSpan.FromMilliseconds(1),
            AccountKey = () => _account,
        };
        _cloud.Watch("Portátil reaberto", reaberto);
        reaberto.RequestPairing();
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 1, "o pedido no transporte");
        Assert.NotNull(reaberto.Status.Pairing);

        // A ronda seguinte vê que a sessão é de outra conta e põe a sincronização em pausa: o pedido para.
        Assert.True(await reaberto.RunAsync(force: true));
        Assert.Equal(SyncPhase.Stopped, reaberto.Status.Phase);
        Assert.Null(reaberto.Status.Pairing);
        await Task.Delay(50);
        var reads = novo.Transport.Reads;
        await Task.Delay(100);
        Assert.InRange(novo.Transport.Reads, reads, reads + 2);
    }

    // ---- A-06 (4): «Não fui eu» diz se recusou -----------------------------------------------------------------------

    [Fact]
    public async Task NotMe_SaysWhetherItRefused()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        novo.Agent.ConfirmPairing();
        // Com uma aprovação a meio (a porta do Windows aberta) nada é recusado.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approving = casa.Agent.ApproveWithCodeAsync(session, code, () => gate.Task);
        Assert.False(await casa.Agent.RefuseAsync(session));
        Assert.Null(casa.Agent.BeginRefuse(session));
        gate.SetResult(false);
        Assert.Equal(PairingAnswer.NotConfirmed, await approving);
        // Um pedido que não existe também não.
        Assert.False(await casa.Agent.RefuseAsync("NAOEXISTE"));
        // Sem aprovação a meio: recusa (a decisão é imediata; a escrita da recusa vem a seguir).
        var write = casa.Agent.BeginRefuse(session);
        Assert.NotNull(write);
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.False(_cloud.Has(SyncPaths.Pairing(session) + PairingProtocol.Refused));
        await write!(CancellationToken.None);
        Assert.True(_cloud.Has(SyncPaths.Pairing(session) + PairingProtocol.Refused));
        Assert.False(await casa.Agent.RefuseAsync(session));

        // A barra só mostra «Recusado…» quando a recusa aconteceu.
        var root = GoogleSyncTests.FindRoot();
        var glue = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "MainWindow.PairingBar.cs"));
        Assert.Contains("if (Sync.SyncService.RefusePairing(session)) _pairingAdvice = PairingBarModel.RefusedAdvice;", glue);
        var service = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.Contains("public static bool RefusePairing(string session)", service);
    }

    // ---- A-07: a primeira ronda a seguir à aprovação, e uma ronda cuja contagem dos pedidos falha -------------------

    private List<(string Name, bool Ok)> Rounds()
    {
        lock (_rounds) return [.. _rounds];
    }

    // A conta chegou e a entrada correu bem, mas a primeira ronda falha (a rede caiu): fica à vista como qualquer ronda
    // falhada — o cartão deixa de dizer «entrou na conta. A sincronizar…» — e o anfitrião é avisado para marcar nova
    // tentativa (em vez de esperar pela ronda periódica).
    [Fact]
    public async Task FirstRound_FailingRightAfterTheApproval_ShowsTheError_AndAsksForARetry()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        novo.Agent.ConfirmPairing();
        // A listagem da conta (só existe dentro da ronda, depois de entrar) falha uma vez.
        novo.Transport.FailList(casa.Store.Load()!.Engine!.ChainId + "/", 1, () => new IOException("pasta inacessível"));
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await _cloud.WaitFor(() => Rounds().Count == 1, "o fim da primeira ronda");
        var status = novo.Agent.Status;
        Assert.Equal(SyncPhase.On, status.Phase);
        Assert.False(status.Running);
        Assert.NotNull(status.Error);
        Assert.Null(status.Pairing);
        Assert.NotEqual(GuestPairingKind.Joined, GuestPairingModel.From(status).Kind);
        Assert.Equal([("Portátil", false)], Rounds());

        // A tentativa seguinte (a do agendador) corre bem: nada se perdeu.
        Assert.True(await novo.Agent.RunAsync());
        Assert.Null(novo.Agent.Status.Error);
        Assert.False(novo.Agent.Status.JoinedNow);
        Assert.True(await casa.Agent.RunAsync());
        Assert.True(await novo.Agent.RunAsync());
        Assert.Equal("Wikipédia", novo.Profile.Title("https://pt.wikipedia.org/"));
        Assert.Equal("Do portátil", casa.Profile.Title("https://portatil.example/"));
    }

    // Quando corre bem, o anfitrião também fica a saber (a ronda não passou pelo agendador).
    [Fact]
    public async Task FirstRound_AfterTheApproval_TellsTheHostItEnded()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        novo.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await _cloud.WaitFor(() => Rounds().Count == 1, "o fim da primeira ronda");
        Assert.Equal([("Portátil", true)], Rounds());
        Assert.Null(novo.Agent.Status.Error);

        // No navegador o sinal acerta o agendador (nova tentativa depois de uma falha, o período depois de correr bem).
        var service = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.Contains("RoundEnded = ok =>", service);
        Assert.Contains("Schedule.Done(Now(), ok);", service);
    }

    // Contar os pedidos de computadores novos é o último passo de uma ronda: se essa listagem falhar, a ronda — que já
    // enviou e recebeu tudo — conta como boa.
    [Fact]
    public async Task Round_ThatAlreadySynced_IsNotAFailure_WhenCountingTheRequestsFails()
    {
        var (casa, _) = await TwoAsync();
        _now += 60_000;
        casa.Profile.Bookmark("https://exemplo.example/", "Novo");
        casa.Transport.FailList("pair/", 1, () => new IOException("pasta inacessível"));
        Assert.True(await casa.Agent.RunAsync());
        Assert.Null(casa.Agent.Status.Error);
        Assert.Equal(_now, casa.Agent.Status.LastSuccessMs);
        Assert.Equal(0, casa.Transport.ListFaultsLeft);
        // A sessão da Google por renovar não passa calada: a ronda falha e o estado pede para entrar outra vez.
        _now += 60_000;
        casa.Transport.FailList("pair/", 1, () => new Eteris.Sync.Google.OAuthException("A sessão da Google acabou.", needsSignIn: true));
        Assert.False(await casa.Agent.RunAsync());
        Assert.True(casa.Agent.Status.NeedsSignIn);
    }

    // ---- o transporte dos testes: conta leituras, falha ou segura uma escrita ----------------------------------------

    private sealed class ScriptedTransport(ISyncTransport inner, Action pulse) : ISyncTransport
    {
        private readonly Lock _gate = new();
        private readonly List<(string End, Func<Exception> Make)> _failWrites = [];
        private readonly List<(string End, Func<Exception> Make, int Left)> _failReads = [];
        private readonly List<(string End, Func<Exception> Make, int Left)> _failLists = [];
        private readonly List<Hold> _holds = [];
        private int _reads;

        public sealed class Hold(string end)
        {
            private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string End { get; } = end;
            public Task Arrived => _arrived.Task;
            internal Task Wait()
            {
                _arrived.TrySetResult();
                return _release.Task;
            }
            public void Release() => _release.TrySetResult();
            public void Fail(Exception e) => _release.TrySetException(e);
        }

        public int Reads => Volatile.Read(ref _reads);

        public string Name => inner.Name;

        // A próxima escrita cujo caminho acaba em «end» falha (uma vez).
        public void FailWrite(string end, Func<Exception> make)
        {
            lock (_gate) _failWrites.Add((end, make));
        }

        // As próximas «times» leituras cujo caminho acaba em «end» falham.
        public void FailRead(string end, int times, Func<Exception> make)
        {
            lock (_gate) _failReads.Add((end, make, times));
        }

        // A próxima escrita cujo caminho acaba em «end» fica à espera (uma vez).
        public Hold HoldWrite(string end)
        {
            var hold = new Hold(end);
            lock (_gate) _holds.Add(hold);
            return hold;
        }

        // As próximas «times» listagens cujo prefixo acaba em «end» falham.
        public void FailList(string end, int times, Func<Exception> make)
        {
            lock (_gate) _failLists.Add((end, make, times));
        }

        public int ListFaultsLeft
        {
            get { lock (_gate) return _failLists.Sum(f => f.Left); }
        }

        public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            Exception? failure = null;
            lock (_gate)
            {
                var at = _failLists.FindIndex(f => f.Left > 0 && prefix.EndsWith(f.End, StringComparison.Ordinal));
                if (at >= 0)
                {
                    var fault = _failLists[at];
                    _failLists[at] = (fault.End, fault.Make, fault.Left - 1);
                    failure = fault.Make();
                }
            }
            if (failure is not null)
            {
                pulse();
                throw failure;
            }
            return await inner.ListAsync(prefix, cancellationToken);
        }

        public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            Exception? failure = null;
            lock (_gate)
            {
                var at = _failReads.FindIndex(f => f.Left > 0 && path.EndsWith(f.End, StringComparison.Ordinal));
                if (at >= 0)
                {
                    var fault = _failReads[at];
                    _failReads[at] = (fault.End, fault.Make, fault.Left - 1);
                    failure = fault.Make();
                }
            }
            if (failure is not null)
            {
                pulse();
                throw failure;
            }
            return await inner.ReadAsync(path, cancellationToken);
        }

        public async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            Exception? failure = null;
            Hold? hold = null;
            lock (_gate)
            {
                var at = _failWrites.FindIndex(f => path.EndsWith(f.End, StringComparison.Ordinal));
                if (at >= 0)
                {
                    failure = _failWrites[at].Make();
                    _failWrites.RemoveAt(at);
                }
                else if (_holds.Find(h => path.EndsWith(h.End, StringComparison.Ordinal)) is { } found)
                {
                    hold = found;
                    _holds.Remove(found);
                }
            }
            if (failure is not null) throw failure;
            if (hold is not null) await hold.Wait();
            await inner.WriteAsync(path, data, cancellationToken);
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);
    }
}
