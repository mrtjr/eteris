using System.IO;
using System.Text.Json;
using Eteris.Sync.Engine;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Aprovar um computador novo escrevendo o código (0.2.15, T8, «number matching»): o computador que já está na conta
// (aqui «Casa») nunca mostra o código de verificação; pede-o. O computador novo mostra-o e a pessoa escreve-o na Casa.
// Três códigos errados recusam o pedido; «Não fui eu» também. Dois agentes na mesma pasta de teste, como em
// SyncAgentTests.
public sealed class PairingNumberMatchingTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-pair-code-" + Guid.NewGuid().ToString("N"));

    private long Now() => _now;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store);

    private Device NewDevice(string name, MemoryStore? store = null, MemoryProfile? profile = null, Func<ISyncTransport>? transport = null)
    {
        profile ??= new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport ?? (() => new FolderTransport(_folder)), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) };
        return new Device(agent, profile, store);
    }

    private FolderTransport Folder => new(_folder);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "À espera de: " + what);
    }

    private async Task<bool> HasStep(string session, string step) => await Folder.ReadAsync(SyncPaths.Pairing(session) + step) is not null;

    private async Task<int> RequestFiles()
        => (await Folder.ListAsync(SyncPaths.Root + "pair/")).Count(f => f.EndsWith("/" + PairingProtocol.Request, StringComparison.Ordinal));

    // A Casa cria a conta (e guarda o código de recuperação).
    private async Task<Device> HostAsync(string name = "Casa")
    {
        var casa = NewDevice(name);
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        await casa.Agent.StartAsync();
        casa.Agent.AcknowledgeCode();
        return casa;
    }

    // Um computador novo na mesma conta pede para entrar.
    private async Task<Device> GuestAsync(string name)
    {
        var before = await RequestFiles();
        var guest = NewDevice(name);
        guest.Profile.Bookmark("https://portatil.example/", "Do portátil");
        await guest.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, guest.Agent.Status.Phase);
        guest.Agent.RequestPairing();
        for (var i = 0; i < 1000 && await RequestFiles() == before; i++) await Task.Delay(10);
        Assert.True(await RequestFiles() > before, "o pedido na pasta");
        return guest;
    }

    // O pedido pronto na Casa: respondeu, o computador novo mostra o código, a Casa espera que o escrevam.
    private async Task<(Device Casa, Device Guest, string Session, string Code)> ReadyAsync(string guestName = "Portátil")
    {
        var casa = await HostAsync();
        var guest = await GuestAsync(guestName);
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1 && guest.Agent.Status.Pairing?.Code is not null, "o pedido pronto");
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
        var session = await PairingRequests.OpenAsync(Folder, Now);
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
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 2 && portatil.Agent.Status.Pairing?.Code is not null
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
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1, "o primeiro pedido pronto");
        _now += 1_000;
        var tablet = await GuestAsync("Tablet");
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 2, "os dois pedidos prontos");
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
        await casa.Agent.RefuseAsync(session);
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
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 1 && guest.Agent.Status.Pairing?.Code is not null, "o pedido pronto na Casa");
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

    // O código de recuperação da conta da Casa: AcknowledgeCode só o tira do ecrã; o segredo guardado dá-o outra vez.
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
        var future = await PairingRequests.OpenAsync(Folder, () => _now + 180_000);
        await casa.Agent.CheckRequestsAsync();
        Assert.Equal(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.False(await HasStep(future, PairingProtocol.HostKey));

        var ahead = await PairingRequests.OpenAsync(Folder, () => _now + 60_000);
        await casa.Agent.CheckRequestsAsync();
        Assert.NotEqual(0, casa.Agent.Status.AnsweringSinceMs);
        Assert.True(await HasStep(ahead, PairingProtocol.HostKey));
        Assert.False(await HasStep(future, PairingProtocol.HostKey));
    }

    // O nome do computador novo chega do transporte: limpo ao mostrar (a limpeza é da T9; isto é regressão).
    [Fact]
    public async Task GuestName_IsCleanedWhenShown()
    {
        var (casa, _, session, _) = await ReadyAsync("Portátil‮gpj.exe\r\nSegunda linha");
        var name = casa.Agent.Status.Requests[0].Name;
        Assert.DoesNotContain('‮', name);
        Assert.DoesNotContain('\r', name);
        Assert.DoesNotContain('\n', name);
        Assert.DoesNotContain(name, char.IsControl);
        Assert.StartsWith("Portátil", name, StringComparison.Ordinal);

        var blank = await GuestAsync("   ");
        await casa.Agent.CheckRequestsAsync();
        await Until(() => casa.Agent.Status.Requests.Count == 2, "o segundo pedido pronto");
        Assert.Equal("Aparelho sem nome", casa.Agent.Status.Requests.Single(r => r.Session != session).Name);
        GC.KeepAlive(blank);
    }
}
