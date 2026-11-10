using System.IO;
using System.Reflection;
using System.Text.Json;
using Eteris.Browser.Sync;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Aprovar um computador novo escrevendo o código (0.2.15, T8, «number matching»): o computador que já está na conta
// (aqui «Casa») nunca mostra o código de verificação; pede-o. O computador novo mostra-o e a pessoa escreve-o na Casa.
// Três códigos errados recusam o pedido; «Não fui eu» também. Os agentes de cada teste partilham um transporte em
// memória (sem disco: uma pasta real dava erros passageiros sob carga que matavam os fluxos de emparelhamento) e as
// esperas acordam com sinais (Changed dos agentes e escritas no transporte), nunca com um orçamento de tempo.
public sealed class PairingNumberMatchingTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);   // só contra bloqueio; nunca é o caminho normal
    private long _now = 1_762_560_000_000;
    private readonly MemoryTransport _memory = new();
    private readonly SignalTransport _cloud;
    private readonly SemaphoreSlim _pulse = new(0);
    private readonly List<(string Name, Device Device)> _devices = [];

    public PairingNumberMatchingTests() => _cloud = new SignalTransport(_memory, Pulse);

    private long Now() => _now;

    // O Changed nunca lança: Pulse só liberta o semáforo (que nunca se descarta: escritas de fundo ainda em curso, como
    // o x-host de um cancelamento, podem avisar depois do fim do teste).
    private void Pulse() => _pulse.Release();

    // No fim, todos os agentes param (nenhum ciclo de sondagem fica vivo). Só escreve no MemoryStore de cada um.
    public void Dispose()
    {
        foreach (var (_, device) in _devices)
        {
            device.Agent.Changed -= Pulse;
            device.Agent.SignOut(forgetKey: false);
        }
    }

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store);

    private Device NewDevice(string name, MemoryStore? store = null, MemoryProfile? profile = null, Func<ISyncTransport>? transport = null)
    {
        profile ??= new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport ?? (() => _cloud), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) };
        var device = new Device(agent, profile, store);
        _devices.Add((name, device));
        agent.Changed += Pulse;
        return device;
    }

    private SignalTransport Cloud => _cloud;

    // Espera até a condição se cumprir. Acorda em cada Changed de um agente deste teste e em cada escrita no transporte;
    // a condição avalia-se sempre AQUI (nunca dentro do Changed: lá dentro um agente pode ter o seu trinco). Um aviso
    // entre a avaliação e o WaitAsync fica contado no semáforo (não se perde).
    private async Task WaitFor(Func<bool> condition, string what)
    {
        var guard = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            var left = HangGuard - guard.Elapsed;
            if (left <= TimeSpan.Zero) Assert.Fail("À espera de: " + what + " | " + Describe());
            await _pulse.WaitAsync(left);
        }
    }

    // Diagnóstico de uma espera que não acabou: o estado de cada agente (nunca o valor do código) e os ficheiros do
    // emparelhamento.
    private string Describe()
    {
        var parts = _devices.Select(d =>
        {
            var s = d.Device.Agent.Status;
            return $"{d.Name}: Phase={s.Phase} Running={s.Running} Code={(s.Pairing?.Code is null ? "não" : "sim")} "
                + $"Error={s.Pairing?.Error ?? "-"} End={s.Pairing?.End.ToString() ?? "-"} Requests={s.Requests.Count} "
                + $"Answering={s.AnsweringSinceMs != 0} Waiting={s.WaitingRequests} Quiet={s.PairingQuiet} Bad={BadCount(d.Device)}";
        });
        var files = _memory.Files.Keys.Where(k => k.StartsWith(SyncPaths.Root + "pair/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        return string.Join(" ; ", parts) + " | pair/: " + string.Join(", ", files);
    }

    private Task Until(Func<bool> condition, string what) => WaitFor(condition, what);

    // Responde aos pedidos (já escritos: os testes esperam o 0-request antes) e espera pela condição. Uma procura
    // chega: em memória a lista não falha, e o 1-host fica escrito antes de CheckRequestsAsync voltar.
    private async Task AnswerUntil(Device casa, Func<bool> condition, string what)
    {
        await casa.Agent.CheckRequestsAsync();
        await WaitFor(condition, what);
    }

    private bool HasStepNow(string session, string step) => _memory.Files.ContainsKey(SyncPaths.Pairing(session) + step);

    private Task<bool> HasStep(string session, string step) => Task.FromResult(HasStepNow(session, step));

    private int RequestFiles()
        => _memory.Files.Keys.Count(f => f.StartsWith(SyncPaths.Root + "pair/", StringComparison.Ordinal) && f.EndsWith("/" + PairingProtocol.Request, StringComparison.Ordinal));

    // A Casa cria a conta (e guarda o código de recuperação).
    private async Task<Device> HostAsync(string name = "Casa")
    {
        var casa = NewDevice(name);
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        await casa.Agent.StartAsync();
        casa.Agent.MarkKitSaved();
        return casa;
    }

    // Um computador novo na mesma conta pede para entrar.
    private async Task<Device> GuestAsync(string name)
    {
        var before = RequestFiles();
        var guest = NewDevice(name);
        guest.Profile.Bookmark("https://portatil.example/", "Do portátil");
        await guest.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, guest.Agent.Status.Phase);
        guest.Agent.RequestPairing();
        await WaitFor(() => RequestFiles() > before, "o pedido na pasta");
        return guest;
    }

    // O pedido pronto na Casa: respondeu, o computador novo mostra o código, a Casa espera que o escrevam.
    private async Task<(Device Casa, Device Guest, string Session, string Code)> ReadyAsync(string guestName = "Portátil")
    {
        var casa = await HostAsync();
        var guest = await GuestAsync(guestName);
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && guest.Agent.Status.Pairing?.Code is not null, "o pedido pronto");
        return (casa, guest, casa.Agent.Status.Requests[0].Session, guest.Agent.Status.Pairing!.Code!);
    }

    // Um código de 6 algarismos que não é o certo.
    private static string Wrong(string code, int shift = 1)
    {
        var value = (int.Parse(code.Replace(" ", ""), System.Globalization.CultureInfo.InvariantCulture) + shift) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Host_NeverExposesTheCode()
    {
        Assert.Null(typeof(HostRequest).GetProperty("Code"));
        var (casa, _, _, code) = await ReadyAsync();
        var json = JsonSerializer.Serialize(casa.Agent.Status);
        Assert.DoesNotContain(code, json, StringComparison.Ordinal);
        Assert.DoesNotContain(code.Replace(" ", ""), json, StringComparison.Ordinal);
        Assert.Equal(PairingCode.MaxWrong, casa.Agent.Status.Requests[0].AttemptsLeft);
        // Nem o estado das páginas internas nem a linha do menu levam o código.
        var service = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.DoesNotContain("r.Code", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Requests[0].Code", service, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RightCode_DeliversTheKey_AndTheGuestJoins()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        guest.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.Contains("foi aprovado", casa.Agent.Status.Notice);
        for (var round = 0; round < 3; round++)
        {
            Assert.True(await casa.Agent.RunAsync());
            Assert.True(await guest.Agent.RunAsync());
        }
        Assert.Equal("Do portátil", casa.Profile.Title("https://portatil.example/"));
        Assert.Equal("Wikipédia", guest.Profile.Title("https://pt.wikipedia.org/"));
    }

    [Fact]
    public async Task RightCode_WithoutSpaces_Works()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        guest.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code.Replace(" ", "")));
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
    }

    [Fact]
    public async Task WrongCode_ThreeTimes_RefusesAndRemovesTheRequest()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, 1)));
        Assert.Equal(2, casa.Agent.Status.Requests[0].AttemptsLeft);
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, 2)));
        Assert.Equal(1, casa.Agent.Status.Requests[0].AttemptsLeft);
        Assert.Equal(PairingAnswer.Refused, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, 3)));
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.True(await HasStep(session, PairingProtocol.Refused));
        Assert.False(await HasStep(session, PairingProtocol.Sealed));
        await Until(() => guest.Agent.Status.Pairing?.Error is not null, "a recusa no portátil");
        Assert.Contains("recusou", guest.Agent.Status.Pairing!.Error);
        Assert.Equal(SyncPhase.Choose, guest.Agent.Status.Phase);
        // Depois de recusado, nem o código certo serve.
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(session, code));
        Assert.False(await HasStep(session, PairingProtocol.Sealed));
    }

    [Fact]
    public async Task WrongCode_ThenRight_Approves()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, 1)));
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, 2)));
        guest.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
    }

    [Fact]
    public async Task Incomplete_DoesNotCount()
    {
        var (casa, _, session, _) = await ReadyAsync();
        foreach (var typed in new[] { "12", "", "abc", "12345", "1234567" })
            Assert.Equal(PairingAnswer.Incomplete, await casa.Agent.ApproveWithCodeAsync(session, typed));
        Assert.Equal(PairingCode.MaxWrong, casa.Agent.Status.Requests[0].AttemptsLeft);
    }

    [Fact]
    public async Task Gate_Refused_KeepsTheRequest_AndCountsNothing()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        Assert.Equal(PairingAnswer.NotConfirmed, await casa.Agent.ApproveWithCodeAsync(session, code, () => Task.FromResult(false)));
        Assert.Single(casa.Agent.Status.Requests);
        Assert.Equal(PairingCode.MaxWrong, casa.Agent.Status.Requests[0].AttemptsLeft);
        Assert.False(await HasStep(session, PairingProtocol.Sealed));
        guest.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code, () => Task.FromResult(true)));
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
    }

    [Fact]
    public async Task Gate_RunsOnlyAfterTheRightCode()
    {
        var (casa, _, session, code) = await ReadyAsync();
        var asked = 0;
        Task<bool> Gate()
        {
            asked++;
            return Task.FromResult(false);
        }
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code), Gate));
        Assert.Equal(PairingAnswer.Incomplete, await casa.Agent.ApproveWithCodeAsync(session, "12", Gate));
        Assert.Equal(0, asked);
        Assert.Equal(PairingAnswer.NotConfirmed, await casa.Agent.ApproveWithCodeAsync(session, code, Gate));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Approve_WithoutACode_IsImpossible()
    {
        Assert.Null(typeof(SyncAgent).GetMethod("ApproveAsync", [typeof(string), typeof(CancellationToken)]));
        Assert.DoesNotContain(typeof(SyncAgent).GetMethods(), m => m.Name == "ApproveAsync");
        Assert.NotNull(typeof(SyncAgent).GetMethod(nameof(SyncAgent.ApproveWithCodeAsync)));
    }

    [Fact]
    public async Task ForgedRequest_ShowsNothingToCopy()
    {
        var casa = await HostAsync();
        // Quem controla o transporte escreve um pedido à mão; nenhum computador novo o segue.
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await casa.Agent.CheckRequestsAsync();
        Assert.True(await HasStep(session, PairingProtocol.HostKey));
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.NotEqual(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(session, "123 456"));
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync("NAOEXISTE", "123 456"));
    }

    [Fact]
    public async Task TwoRequests_TheCodeOfTheSecondDoesNotApproveTheFirst()
    {
        var casa = await HostAsync();
        var portatil = await GuestAsync("Portátil");
        var tablet = await GuestAsync("Tablet");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 2 && portatil.Agent.Status.Pairing?.Code is not null
            && tablet.Agent.Status.Pairing?.Code is not null, "os dois pedidos prontos");
        var first = casa.Agent.Status.Requests.Single(r => r.Name == "Portátil").Session;
        var second = tablet.Agent.Status.Pairing!.Code!;
        Assert.NotEqual(portatil.Agent.Status.Pairing!.Code, second);
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(first, second));
        Assert.False(await HasStep(first, PairingProtocol.Sealed));
    }

    [Fact]
    public async Task Expired_IsGone()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        guest.Agent.ConfirmPairing();
        _now += (long)PairingProtocol.Lifetime.TotalMilliseconds + 1;
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(session, code));
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.False(await HasStep(session, PairingProtocol.Sealed));
    }

    [Fact]
    public async Task Requests_AreListedOldestFirst()
    {
        var casa = await HostAsync();
        var portatil = await GuestAsync("Portátil");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1, "o primeiro pedido pronto");
        _now += 1_000;
        var tablet = await GuestAsync("Tablet");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 2, "os dois pedidos prontos");
        Assert.Equal(["Portátil", "Tablet"], casa.Agent.Status.Requests.Select(r => r.Name));
        Assert.True(casa.Agent.Status.Requests[0].SeenMs < casa.Agent.Status.Requests[1].SeenMs);
        GC.KeepAlive(portatil);
        GC.KeepAlive(tablet);
    }

    // «Não fui eu»: a recusa vai para o outro lado (x-host) e o pedido sai.
    [Fact]
    public async Task NotMe_WritesRefusal_AndRemovesTheRequest()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        Assert.True(await casa.Agent.RefuseAsync(session));
        Assert.True(await HasStep(session, PairingProtocol.Refused));
        Assert.False(await HasStep(session, PairingProtocol.Sealed));
        Assert.Empty(casa.Agent.Status.Requests);
        await Until(() => guest.Agent.Status.Pairing?.Error is not null, "a recusa no portátil");
        Assert.Contains("recusou", guest.Agent.Status.Pairing!.Error);
        Assert.Equal(SyncPhase.Choose, guest.Agent.Status.Phase);
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(session, code));
    }

    // O Éteris da Casa reabriu a meio: a sessão a que já respondeu morre (não volta a responder nem aceita o código).
    [Fact]
    public async Task Restart_KillsTheSession_NoNewAttempts()
    {
        var (casa, _, session, code) = await ReadyAsync();
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, 1)));
        Assert.Equal(PairingAnswer.WrongCode, await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, 2)));
        var reaberta = NewDevice("Casa", casa.Store, casa.Profile);
        Assert.True(await reaberta.Agent.RunAsync());
        await reaberta.Agent.CheckRequestsAsync();
        Assert.Empty(reaberta.Agent.Status.Requests);
        Assert.Equal(0, reaberta.Agent.Status.AnsweringSinceMs);
        Assert.Equal(PairingAnswer.Gone, await reaberta.Agent.ApproveWithCodeAsync(session, code));
        Assert.False(await HasStep(session, PairingProtocol.Sealed));
    }

    // Dois computadores na conta: o primeiro que respondeu é o único que pode aprovar essa sessão.
    [Fact]
    public async Task SecondHost_CannotApproveASessionAnsweredByTheFirst()
    {
        var casa = await HostAsync();
        var escritorio = NewDevice("Escritório");
        await escritorio.Agent.StartAsync();
        await casa.Agent.RunAsync();
        // A Casa já confirmou que guardou o código; entra-se pelo código de recuperação (ainda o da criação).
        var recovery = await RecoveryCodeOf(casa);
        await escritorio.Agent.JoinWithCodeAsync(recovery);
        Assert.Equal(SyncPhase.On, escritorio.Agent.Status.Phase);

        var guest = await GuestAsync("Portátil");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && guest.Agent.Status.Pairing?.Code is not null, "o pedido pronto na Casa");
        var session = casa.Agent.Status.Requests[0].Session;
        var code = guest.Agent.Status.Pairing!.Code!;

        await escritorio.Agent.CheckRequestsAsync();
        Assert.Empty(escritorio.Agent.Status.Requests);
        Assert.Equal(0, escritorio.Agent.Status.AnsweringSinceMs);
        Assert.Equal(PairingAnswer.Gone, await escritorio.Agent.ApproveWithCodeAsync(session, code));

        guest.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
    }

    // O código de recuperação (formato antigo) da conta da Casa, a partir do segredo guardado.
    private static Task<string> RecoveryCodeOf(Device casa)
    {
        var secret = casa.Store.Load()!.Secret!;
        using var account = Eteris.Sync.Crypto.AccountSecret.FromBytes(Eteris.Sync.Crypto.Kdf.FromBase64Url(secret));
        return Task.FromResult(account.ToCode());
    }

    // Um pedido com a hora no futuro: quem escreve o ficheiro escolhe a hora; a Casa só aceita 2 minutos de diferença.
    [Fact]
    public async Task Request_FromTheFuture_IsIgnored()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), PairingProtocol.ClockSkew);
        var casa = await HostAsync();
        var future = await PairingRequests.OpenAsync(Cloud, () => _now + 180_000);
        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.False(await HasStep(future, PairingProtocol.HostKey));

        var ahead = await PairingRequests.OpenAsync(Cloud, () => _now + 60_000);
        await casa.Agent.CheckRequestsAsync();
        Assert.NotEqual(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.True(await HasStep(ahead, PairingProtocol.HostKey));
        Assert.False(await HasStep(future, PairingProtocol.HostKey));
    }

    // O nome do computador novo chega do transporte: limpo ao mostrar (a limpeza é da T9; isto é regressão).
    [Fact]
    public async Task GuestName_IsCleanedWhenShown()
    {
        var (casa, _, session, _) = await ReadyAsync("Portátil\u202Egpj.exe\r\nSegunda linha");
        var name = casa.Agent.Status.Requests[0].Name;
        Assert.DoesNotContain('\u202E', name);
        Assert.DoesNotContain('\r', name);
        Assert.DoesNotContain('\n', name);
        Assert.DoesNotContain(name, char.IsControl);
        Assert.StartsWith("Portátil", name, StringComparison.Ordinal);

        var blank = await GuestAsync("   ");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 2, "o segundo pedido pronto");
        Assert.Equal("Aparelho sem nome", casa.Agent.Status.Requests.Single(r => r.Session != session).Name);
        GC.KeepAlive(blank);
    }

    // Um computador novo com o cliente mudado escolhe como nome o próprio código: só o conhece no passo 4, mas o nome
    // segue selado nesse mesmo passo. A barra da Casa nunca o mostra (as páginas internas só levam a linha da barra,
    // limpa e cortada).
    [Fact]
    public async Task GuestNamedAfterTheCode_TheBarShowsNoDigits()
    {
        var casa = await HostAsync();
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        using var guest = await PairingRequests.WaitForHostAsync(Cloud, session, "provisório", Now, Quick, TimeSpan.FromMilliseconds(5));
        // O cliente mudado espera pelo nonce da Casa, calcula o código e só então fixa o nome.
        await WaitFor(() => HasStepNow(session, PairingProtocol.HostNonce), "o nonce da Casa");
        var nonceFile = await Cloud.ReadAsync(SyncPaths.Pairing(session) + PairingProtocol.HostNonce);
        Assert.NotNull(nonceFile);
        var hostNonce = Kdf.FromBase64Url(Json.Parse<PairingMessage>(nonceFile).Nonce!);
        const BindingFlags inner = BindingFlags.Instance | BindingFlags.NonPublic;
        byte[] Field(string name) => (byte[])typeof(PairingGuest).GetField(name, inner)!.GetValue(guest)!;
        var guestKey = (byte[])typeof(PairingSide).GetProperty("PublicKey", inner)!.GetValue(guest)!;
        var sas = PairingProtocol.Sas(PairingProtocol.Transcript(session, Field("_hostKey"), guestKey, hostNonce, Field("_guestNonce")));
        typeof(PairingGuest).GetField("_name", inner)!.SetValue(guest, sas);
        Assert.Equal(sas, await guest.WaitForCodeAsync());

        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1, "o pedido pronto");
        Assert.Equal(sas, casa.Agent.Status.Requests[0].Name);    // chegou como quem pede quis
        var view = PairingBarModel.From(casa.Agent.Status, _now, PairingBarModel.DefaultReceives);
        Assert.Equal(PairingBarKind.Code, view.Kind);
        Assert.Equal("Aparelho sem nome", view.Name);
        Assert.DoesNotContain(PairingBarModel.NameLine(view.Name!), char.IsDigit);
        var service = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.DoesNotContain("name = r.Name", service, StringComparison.Ordinal);
        Assert.Contains("nameLine = PairingBarModel.NameLine(r.Name)", service, StringComparison.Ordinal);
    }

    // ---- o pedido que acaba no computador novo depois de pronto (revisão final, A-03) ------------------------------

    // No computador novo a pessoa responde «Não» (ou o tempo acaba) e pede outra vez: na Casa o pedido velho sai da
    // barra sozinho, sem gesto, e o que ela mostra é o pedido novo — o código novo serve à primeira.
    [Fact]
    public async Task ReadyRequest_CancelledOnTheNewComputer_LeavesTheBar_AndTheNewOneShows()
    {
        var (casa, guest, old, _) = await ReadyAsync();
        guest.Agent.CancelPairing();
        await Until(() => casa.Agent.Status.Requests.Count == 0 && BadCount(casa) == 1, "o pedido cancelado fora da barra");
        Assert.Equal(PairingBarKind.None, PairingBarModel.From(casa.Agent.Status, _now, PairingBarModel.DefaultReceives).Kind);
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(old, "123 456"));

        var before = RequestFiles();
        guest.Agent.RequestPairing();
        await WaitFor(() => RequestFiles() > before, "o pedido novo");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && guest.Agent.Status.Pairing?.Code is not null, "o pedido novo pronto");
        var view = PairingBarModel.From(casa.Agent.Status, _now, PairingBarModel.DefaultReceives);
        Assert.Equal(PairingBarKind.Code, view.Kind);
        Assert.NotEqual(old, view.Session);
        Assert.Equal(PairingCode.MaxWrong, view.AttemptsLeft);
        guest.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(view.Session!, guest.Agent.Status.Pairing!.Code!));
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
        Assert.Equal(0, BadCount(casa));
    }

    // O código certo num pedido que o outro lado cancelou entretanto (o x-host já lá está, ainda ninguém o viu): a Casa
    // confere a recusa antes de entregar. Nada é escrito, ninguém é «aprovado», e o pedido sai.
    [Fact]
    public async Task RightCode_OnARequestCancelledOnTheOtherSide_DeliversNothing()
    {
        var (casa, _, session, code) = await ReadyAsync();
        // A porta do Windows está aberta quando a recusa chega ao transporte (a sondagem deixa o pedido à aprovação).
        async Task<bool> Gate()
        {
            await PairingRequests.RefuseAsync(Cloud, session, Now);
            return true;
        }
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(session, code, Gate));
        Assert.False(await HasStep(session, PairingProtocol.Sealed));
        Assert.DoesNotContain("foi aprovado", casa.Agent.Status.Notice ?? "");
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.Equal(1, BadCount(casa));
        Assert.Equal(PairingAnswer.Gone, await casa.Agent.ApproveWithCodeAsync(session, code));
    }

    // Depois de aprovado, ninguém fica a sondar o transporte por esse pedido.
    [Fact]
    public async Task ReadyRequest_IsFollowedOnlyUntilItEnds()
    {
        var reads = new CountingTransport(_cloud);
        var casa = NewDevice("Casa", transport: () => reads);
        await casa.Agent.StartAsync();
        casa.Agent.MarkKitSaved();
        var guest = await GuestAsync("Portátil");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && guest.Agent.Status.Pairing?.Code is not null, "o pedido pronto");
        var session = casa.Agent.Status.Requests[0].Session;
        // Pronto e à espera do código: a Casa continua a perguntar pela recusa.
        var ready = reads.Refusals(session);
        await Until(() => reads.Refusals(session) >= ready + 3, "a sondagem do pedido pronto");
        guest.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, guest.Agent.Status.Pairing!.Code!));
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
        var after = reads.Refusals(session);
        await Task.Delay(100);
        Assert.InRange(reads.Refusals(session), after, after + 1);
    }

    // Conta as leituras do x-host de cada sessão feitas por este computador (e avisa as esperas do teste).
    private sealed class CountingTransport(SignalTransport inner) : ISyncTransport
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _refusals = new(StringComparer.Ordinal);

        public int Refusals(string session) => _refusals.GetValueOrDefault(SyncPaths.Pairing(session) + PairingProtocol.Refused);

        public string Name => inner.Name;

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (path.EndsWith("/" + PairingProtocol.Refused, StringComparison.Ordinal))
            {
                _refusals.AddOrUpdate(path, 1, (_, n) => n + 1);
                inner.Signal();
            }
            return inner.ReadAsync(path, cancellationToken);
        }

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);
    }

    // ---- o teto dos pedidos respondidos (revisão final, A-04) -------------------------------------------------------

    // Quem escreve no Drive pode abrir muitos pedidos e deixar ficar «pronto» só o que der o código que o computador
    // novo verdadeiro mostra: cada pedido respondido é uma tentativa em 1 000 000. A Casa só acompanha
    // PairingRequests.MaxAnswered de cada vez; os outros ficam só contados e são respondidos quando houver vaga (e uma
    // vaga só se liberta com um pedido que acaba — mal, e conta para os pedidos em série).
    [Fact]
    public async Task ManyRequests_OnlyKAreAnswered()
    {
        Assert.Equal(3, PairingRequests.MaxAnswered);
        var casa = await HostAsync();
        var sessions = new List<string>();
        for (var i = 0; i < 10; i++) sessions.Add(await PairingRequests.OpenAsync(Cloud, Now));
        int Answered() => sessions.Count(s => HasStepNow(s, PairingProtocol.HostKey));

        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(3, Answered());
        Assert.Equal(7, casa.Agent.Status.WaitingRequests);
        // Procurar outra vez não responde a mais nenhum, e uma ronda só conta.
        await casa.Agent.CheckRequestsAsync();
        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal(3, Answered());
        Assert.Equal(7, casa.Agent.Status.WaitingRequests);

        // Um dos respondidos acaba (cancelado do outro lado: conta): há uma vaga, e o seguinte é respondido.
        var first = sessions.First(s => HasStepNow(s, PairingProtocol.HostKey));
        await PairingRequests.RefuseAsync(Cloud, first, Now);
        await Until(() => BadCount(casa) == 1, "o fim do pedido contado");
        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(4, Answered());
        Assert.Equal(6, casa.Agent.Status.WaitingRequests);

        // Um pedido pronto (com código, à espera de ser escrito) também ocupa a sua vaga.
        var open = sessions.First(s => HasStepNow(s, PairingProtocol.HostKey) && !HasStepNow(s, PairingProtocol.Refused));
        using var guest = await PairingRequests.WaitForHostAsync(Cloud, open, "Portátil", Now, Quick, TimeSpan.FromMilliseconds(5));
        await guest.WaitForCodeAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o pedido pronto");
        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(4, Answered());
    }

    // ---- pedidos em série (R18) ------------------------------------------------------------------------------------

    // Um pedido escrito à mão a que a Casa responde e que o outro lado cancela (x-host) antes de haver código.
    // Espera-se pela contagem, não só pelo fim do fluxo: o agente tira o fluxo da lista (AnsweringSinceMs passa a 0) um
    // instante antes de gravar a contagem.
    private async Task<string> AnsweredThenCancelledElsewhereAsync(Device casa)
    {
        var before = BadCount(casa);
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        Assert.True(await HasStep(session, PairingProtocol.HostKey));
        await PairingRequests.RefuseAsync(Cloud, session, Now);
        await Until(() => casa.Agent.Status.AnsweringSinceMs == 0 && BadCount(casa) == before + 1, "o fim do pedido contado");
        Assert.Empty(casa.Agent.Status.Requests);
        return session;
    }

    private static int BadCount(Device device) => device.Store.Load()?.BadPairings?.Length ?? 0;

    // «Não fui eu» noutra janela quando a aprovação já passou o código (porta do Windows ou entrega): não escreve o
    // x-host nem conta; a aprovação segue (a porta do Windows é onde a pessoa ainda pode desistir).
    [Fact]
    public async Task NotMe_DuringAnApproval_DoesNothing()
    {
        var (casa, guest, session, code) = await ReadyAsync();
        guest.Agent.ConfirmPairing();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approving = casa.Agent.ApproveWithCodeAsync(session, code, () => gate.Task);
        Assert.False(await casa.Agent.RefuseAsync(session));
        Assert.False(await HasStep(session, PairingProtocol.Refused));
        Assert.Equal(0, BadCount(casa));
        gate.SetResult(true);
        Assert.Equal(PairingAnswer.Approved, await approving);
        await Until(() => guest.Agent.Status is { Phase: SyncPhase.On, Running: false }, "o portátil ligado");
        Assert.Equal(0, BadCount(casa));
    }

    // Quem tem o Drive apaga o x-host e o 1-host de uma sessão recusada aqui, deixando só o 0-request: a Casa não
    // responde outra vez à mesma sessão (seriam três tentativas novas sem pedido novo).
    [Fact]
    public async Task RefusedSession_RewrittenOnTheDrive_IsNotAnsweredAgain()
    {
        var casa = await HostAsync();
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        await casa.Agent.RefuseAsync(session);
        await Until(() => casa.Agent.Status.AnsweringSinceMs == 0, "o fim do pedido");
        foreach (var path in await Cloud.ListAsync(SyncPaths.Pairing(session)))
            if (!path.EndsWith("/" + PairingProtocol.Request, StringComparison.Ordinal)) await Cloud.DeleteAsync(path);
        Assert.Equal([session], await PairingRequests.PendingAsync(Cloud, Now));

        await casa.Agent.CheckRequestsAsync();
        await Task.Delay(100);
        Assert.Equal(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.Equal(0, casa.Agent.Status.WaitingRequests);
        Assert.False(await HasStep(session, PairingProtocol.HostKey));
        Assert.Equal(1, BadCount(casa));
    }

    // As sessões acabadas só ficam em memória enquanto o pedido ainda pode voltar a aparecer: cada vez que o 0-request
    // aparece outra vez (reescrito com uma hora nova) a contagem recomeça; Lifetime + ClockSkew sem o ver, sai.
    [Fact]
    public async Task EndedSessions_AreForgotten_OnceTheRequestCanNoLongerBePending()
    {
        var casa = await HostAsync();
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        await casa.Agent.RefuseAsync(session);
        await Until(() => casa.Agent.Status.AnsweringSinceMs == 0, "o fim do pedido");
        foreach (var path in await Cloud.ListAsync(SyncPaths.Pairing(session)))
            if (!path.EndsWith("/" + PairingProtocol.Request, StringComparison.Ordinal)) await Cloud.DeleteAsync(path);
        Assert.Equal(1, Ended(casa));

        // 11 e 20 minutos depois, o pedido reescrito com a hora do momento: nunca respondido (aos 20 já passaram mais de
        // Lifetime + ClockSkew desde que acabou, mas foi visto aos 11).
        foreach (var minutes in new[] { 11, 9 })
        {
            _now += (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;
            await Cloud.WriteAsync(SyncPaths.Pairing(session) + PairingProtocol.Request, Json.Bytes(new PairingMessage { CreatedMs = _now }));
            Assert.Equal([session], await PairingRequests.PendingAsync(Cloud, Now));
            await casa.Agent.CheckRequestsAsync();
            await Task.Delay(100);
            Assert.Equal(0, casa.Agent.Status.AnsweringSinceMs);
            Assert.False(await HasStep(session, PairingProtocol.HostKey));
            Assert.Equal(1, Ended(casa));
        }

        // Mais 13 minutos: o pedido já não pode estar pendente e a sessão sai da memória.
        _now += (long)TimeSpan.FromMinutes(13).TotalMilliseconds;
        Assert.Empty(await PairingRequests.PendingAsync(Cloud, Now));
        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(0, Ended(casa));
        Assert.Equal(1, BadCount(casa));
    }

    private static int Ended(Device device)
    {
        var ended = typeof(SyncAgent).GetField("_ended", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(device.Agent)!;
        return (int)ended.GetType().GetProperty("Count")!.GetValue(ended)!;
    }

    // Um pedido escrito à mão a que a Casa responde e cujo passo 2 (também escrito à mão) não confere.
    private async Task AnsweredThenForgedAsync(Device casa, byte[] guestKeyFile)
    {
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        await Cloud.WriteAsync(SyncPaths.Pairing(session) + PairingProtocol.GuestKey, guestKeyFile);
        await Until(() => casa.Agent.Status.AnsweringSinceMs == 0, "o fim do pedido na Casa");
        Assert.Empty(casa.Agent.Status.Requests);
    }

    // R18 (d): mensagens do pedido que não conferem contam, seja qual for a exceção (chave ilegível, compromisso
    // dirigido a outro anfitrião, ficheiro que não se lê).
    [Fact]
    public async Task Requests_WithForgedMessages_Count()
    {
        var casa = await HostAsync();
        await AnsweredThenForgedAsync(casa, Json.Bytes(new PairingMessage { Key = "!!", Commit = "AA", CreatedMs = _now }));
        await Until(() => BadCount(casa) == 1, "o 1.º contado");
        await AnsweredThenForgedAsync(casa, Json.Bytes(new PairingMessage { Key = "AA", Commit = "AA", CreatedMs = _now, For = "x" }));
        await Until(() => BadCount(casa) == 2, "o 2.º contado");
        Assert.False(casa.Agent.Status.PairingQuiet);
        await AnsweredThenForgedAsync(casa, "{ não é json"u8.ToArray());
        await Until(() => casa.Agent.Status.PairingQuiet, "a barra calada");
        Assert.Equal(3, BadCount(casa));
    }

    [Fact]
    public async Task Requests_InSeries_StopOpeningTheBar()
    {
        // 1.º: «Não fui eu».
        var (casa, _, first, _) = await ReadyAsync();
        await casa.Agent.RefuseAsync(first);
        Assert.Equal(1, BadCount(casa));
        Assert.False(casa.Agent.Status.PairingQuiet);

        // 2.º: três códigos errados.
        var second = await GuestAsync("Tablet");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && second.Agent.Status.Pairing?.Code is not null, "o 2.º pedido pronto");
        var code = second.Agent.Status.Pairing!.Code!;
        var session = casa.Agent.Status.Requests[0].Session;
        for (var shift = 1; shift <= 3; shift++) await casa.Agent.ApproveWithCodeAsync(session, Wrong(code, shift));
        Assert.Equal(2, BadCount(casa));
        Assert.False(casa.Agent.Status.PairingQuiet);

        // 3.º: fica pronto e expira sem aprovação (o relógio da Casa passa os 10 minutos).
        var third = await GuestAsync("Telemóvel");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && third.Agent.Status.Pairing?.Code is not null, "o 3.º pedido pronto");
        _now += (long)PairingProtocol.Lifetime.TotalMilliseconds + 1;
        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(3, BadCount(casa));
        Assert.True(casa.Agent.Status.PairingQuiet);

        // Um 4.º pedido fica pronto, mas a barra não abre sozinha.
        var fourth = await GuestAsync("Portátil novo");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && fourth.Agent.Status.Pairing?.Code is not null, "o 4.º pedido pronto");
        Assert.Equal(PairingBarKind.None, PairingBarModel.From(casa.Agent.Status, _now, PairingBarModel.DefaultReceives).Kind);
        // «Mostrar o pedido na barra» (um gesto) repõe a contagem.
        casa.Agent.ResumePairingBar();
        Assert.False(casa.Agent.Status.PairingQuiet);
        Assert.Equal(PairingBarKind.Code, PairingBarModel.From(casa.Agent.Status, _now, PairingBarModel.DefaultReceives).Kind);

        // Uma aprovação também repõe a contagem.
        await casa.Agent.RefuseAsync(casa.Agent.Status.Requests[0].Session);
        Assert.Equal(1, BadCount(casa));
        var fifth = await GuestAsync("Secretária");
        await AnswerUntil(casa, () => casa.Agent.Status.Requests.Count == 1 && fifth.Agent.Status.Pairing?.Code is not null, "o 5.º pedido pronto");
        fifth.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(casa.Agent.Status.Requests[0].Session, fifth.Agent.Status.Pairing!.Code!));
        Assert.Equal(0, BadCount(casa));
        Assert.Null(casa.Store.Load()!.BadPairings);
    }

    // O outro lado cancela (x-host) depois de a Casa responder: quem escreve pedidos falsos no Drive escreve também o
    // x-host, por isso conta. Três assim, e só assim, calam a barra; a mesma sessão nunca conta duas vezes.
    [Fact]
    public async Task Requests_CancelledOnTheOtherSide_Count_OnceEach()
    {
        var casa = await HostAsync();
        await AnsweredThenCancelledElsewhereAsync(casa);
        await AnsweredThenCancelledElsewhereAsync(casa);
        Assert.Equal(2, BadCount(casa));
        Assert.False(casa.Agent.Status.PairingQuiet);
        await AnsweredThenCancelledElsewhereAsync(casa);
        Assert.True(casa.Agent.Status.PairingQuiet);

        // «Não fui eu» num pedido ainda sem código, seguido do fim do fluxo dessa sessão: soma 1.
        casa.Agent.ResumePairingBar();
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        await casa.Agent.RefuseAsync(session);
        await Until(() => casa.Agent.Status.AnsweringSinceMs == 0, "o fim do pedido");
        await Task.Delay(100);
        Assert.Equal(1, BadCount(casa));
    }

    // Parar o agente a meio de um pedido (sair, fechar) e um erro de transporte não contam. O erro de transporte nem
    // acaba com o pedido: a Casa continua à espera e, quando o transporte volta, o código chega.
    [Fact]
    public async Task Requests_StoppedHere_OrTransportErrors_DoNotCount()
    {
        var flaky = new FlakyTransport(_cloud, Pulse);
        var casa = NewDevice("Casa", transport: () => flaky);
        await casa.Agent.StartAsync();
        casa.Agent.MarkKitSaved();

        // Erro de transporte enquanto espera pelo computador novo: várias sondagens falhadas e o pedido continua.
        var session = await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        flaky.Fail = true;
        var before = flaky.Failures;
        await Until(() => flaky.Failures >= before + 5, "cinco sondagens falhadas");
        Assert.NotEqual(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.Equal(0, BadCount(casa));
        // O transporte volta: o computador novo segue o pedido e o código chega à Casa.
        flaky.Fail = false;
        using var guest = await PairingRequests.WaitForHostAsync(Cloud, session, "Portátil", Now, Quick, TimeSpan.FromMilliseconds(5));
        await guest.WaitForCodeAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o código na Casa");
        Assert.Equal("Portátil", casa.Agent.Status.Requests[0].Name);
        Assert.Equal(0, BadCount(casa));

        // Sair a meio de um pedido (o de cima, já com código, e um novo ainda sem ele).
        await PairingRequests.OpenAsync(Cloud, Now);
        await AnswerUntil(casa, () => casa.Agent.Status.AnsweringSinceMs != 0, "a resposta da Casa");
        casa.Agent.SignOut(forgetKey: false);
        await Task.Delay(100);
        Assert.Equal(0, BadCount(casa));
        Assert.False(casa.Agent.Status.PairingQuiet);
    }

    // A contagem é guardada neste computador (nunca no transporte) e sobrevive ao reinício.
    [Fact]
    public async Task Counter_SurvivesRestart()
    {
        var casa = await HostAsync();
        for (var i = 0; i < PairingFatigue.MaxBad; i++) await AnsweredThenCancelledElsewhereAsync(casa);
        Assert.True(casa.Agent.Status.PairingQuiet);

        var reaberta = NewDevice("Casa", casa.Store, casa.Profile);
        Assert.True(reaberta.Agent.Status.PairingQuiet);
        Assert.Contains("\"badPairs\"", System.Text.Encoding.UTF8.GetString(Eteris.Sync.Format.Json.Bytes(casa.Store.Load()!)));
        foreach (var file in _memory.Files)
        {
            Assert.DoesNotContain("badPairs", file.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("badPairs", System.Text.Encoding.UTF8.GetString(file.Value), StringComparison.Ordinal);
        }
    }

    // Transporte que, com Fail, falha como uma pasta inacessível (e avisa as esperas do teste em cada falha).
    private sealed class FlakyTransport(ISyncTransport inner, Action failed) : ISyncTransport
    {
        private int _failures;

        public volatile bool Fail;

        public int Failures => Volatile.Read(ref _failures);

        public string Name => inner.Name;

        private void Check()
        {
            if (!Fail) return;
            Interlocked.Increment(ref _failures);
            failed();
            throw new IOException("pasta inacessível");
        }

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.ListAsync(prefix, cancellationToken);
        }

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.ReadAsync(path, cancellationToken);
        }

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.WriteAsync(path, data, cancellationToken);
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.DeleteAsync(path, cancellationToken);
        }
    }

    // Transporte em memória que avisa cada escrita e cada apagamento (as esperas acordam com isto, não com o relógio).
    private sealed class SignalTransport(ISyncTransport inner, Action changed) : ISyncTransport
    {
        public string Name => inner.Name;

        public void Signal() => changed();

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);

        public async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(path, data, cancellationToken);
            changed();
        }

        public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            await inner.DeleteAsync(path, cancellationToken);
            changed();
        }
    }
}
