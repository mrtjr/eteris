using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Vault;
using Microsoft.Win32;

namespace Eteris.Browser.Tests.Vault;

// A3 (docs/palavras-passe.md): a reautenticação com o Windows (Windows Hello ou a palavra-passe do Windows) para
// mostrar, copiar, exportar e preencher pagamentos e documentos. O pedido real precisa do PIN do dono: aqui só um
// IReauth falso e as partes que não pedem nada (disponibilidade, IIDs, mapeamentos).
public class WindowsReauthTests
{
    private sealed class FakeReauth(ReauthAvailability availability = ReauthAvailability.Hello) : IReauth
    {
        public int Prompts;
        public ReauthOutcome Outcome = ReauthOutcome.Verified;
        public TaskCompletionSource<ReauthOutcome>? Pending;
        public nint LastOwner;
        public string? LastMessage;

        public ReauthAvailability CheckAvailability() => availability;

        public Task<ReauthOutcome> VerifyAsync(nint owner, string message, CancellationToken cancel)
        {
            Prompts++;
            LastOwner = owner;
            LastMessage = message;
            if (Pending is null) return Task.FromResult(Outcome);
            cancel.Register(() => Pending.TrySetResult(ReauthOutcome.Canceled));
            return Pending.Task;
        }
    }

    private DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private const nint WindowA = 0x1000;
    private const nint WindowB = 0x2000;

    private ReauthGate Gate(FakeReauth fake) => new(fake, () => _now);

    // Janela de graça de 5 minutos por janela do Éteris, como o Firefox, que não se renova a cada uso.
    [Fact]
    public async Task Grace_FiveMinutesPerWindow_NotRenewedByUse()
    {
        var fake = new FakeReauth();
        var gate = Gate(fake);
        Assert.Equal(TimeSpan.FromMinutes(5), ReauthGate.Grace);
        Assert.True(await gate.RequestAsync(WindowA, ReauthPurpose.Reveal));
        Assert.Equal(1, fake.Prompts);
        Assert.Equal(WindowA, fake.LastOwner);
        _now = _now.AddMinutes(4);
        Assert.True(await gate.RequestAsync(WindowA, ReauthPurpose.Reveal));
        Assert.Equal(1, fake.Prompts);
        Assert.True(await gate.RequestAsync(WindowB, ReauthPurpose.Reveal));
        Assert.Equal(2, fake.Prompts);
        _now = _now.AddMinutes(1).AddSeconds(1);
        Assert.False(gate.InGrace(WindowA));
        Assert.True(await gate.RequestAsync(WindowA, ReauthPurpose.Reveal));
        Assert.Equal(3, fake.Prompts);
    }

    // O Windows bloqueado, o computador suspenso ou a janela fechada: a graça recomeça.
    [Fact]
    public async Task Grace_StartsAgain_AfterLockSuspendOrClose()
    {
        var fake = new FakeReauth();
        var gate = Gate(fake);
        await gate.RequestAsync(WindowA, ReauthPurpose.Reveal);
        await gate.RequestAsync(WindowB, ReauthPurpose.Reveal);
        gate.WindowClosed(WindowA);
        Assert.False(gate.InGrace(WindowA));
        Assert.True(gate.InGrace(WindowB));
        gate.Reset();
        Assert.False(gate.InGrace(WindowB));
        Assert.True(ReauthGate.ResetsOn(SessionSwitchReason.SessionLock));
        Assert.True(ReauthGate.ResetsOn(SessionSwitchReason.ConsoleDisconnect));
        Assert.True(ReauthGate.ResetsOn(SessionSwitchReason.RemoteDisconnect));
        Assert.True(ReauthGate.ResetsOn(SessionSwitchReason.SessionLogoff));
        Assert.False(ReauthGate.ResetsOn(SessionSwitchReason.SessionUnlock));
        Assert.True(ReauthGate.ResetsOn(PowerModes.Suspend));
        Assert.False(ReauthGate.ResetsOn(PowerModes.Resume));
    }

    // Exportar, ligar Pagamentos/Documentos na sincronização, aprovar um aparelho: sempre, sem janela de graça.
    [Fact]
    public async Task Always_IgnoresAndDoesNotStartTheGrace()
    {
        var fake = new FakeReauth();
        var gate = Gate(fake);
        await gate.RequestAsync(WindowA, ReauthPurpose.Reveal);
        Assert.True(await gate.RequestAsync(WindowA, ReauthPurpose.Export));
        Assert.Equal(2, fake.Prompts);
        foreach (var always in new[] { ReauthPurpose.Export, ReauthPurpose.SyncSecrets, ReauthPurpose.ApproveDevice, ReauthPurpose.ExportProfileSecrets })
            Assert.True(ReauthPolicy.IsAlways(always), always.ToString());
        var fresh = Gate(fake = new FakeReauth());
        await fresh.RequestAsync(WindowA, ReauthPurpose.Export);
        Assert.False(fresh.InGrace(WindowA));
    }

    [Fact]
    public async Task Refused_IsNoAndStartsNoGrace()
    {
        var fake = new FakeReauth { Outcome = ReauthOutcome.Canceled };
        var gate = Gate(fake);
        Assert.False(await gate.RequestAsync(WindowA, ReauthPurpose.Reveal));
        fake.Outcome = ReauthOutcome.Failed;
        Assert.False(await gate.RequestAsync(WindowA, ReauthPurpose.Reveal));
        Assert.False(gate.InGrace(WindowA));
        Assert.Equal(2, fake.Prompts);
    }

    // A aba que pediu deixou de estar à frente: o pedido é cancelado e não conta.
    [Fact]
    public async Task TabLeavingTheFront_CancelsTheRequest()
    {
        var fake = new FakeReauth { Pending = new() };
        var gate = Gate(fake);
        using var cts = new CancellationTokenSource();
        var request = gate.RequestAsync(WindowA, ReauthPurpose.FillPayment, cts.Token);
        Assert.False(request.IsCompleted);
        cts.Cancel();
        Assert.False(await request);
        Assert.False(gate.InGrace(WindowA));
        // Mesmo que o Windows diga «verificado» depois de a aba sair da frente, não conta.
        fake.Pending = new();
        using var cts2 = new CancellationTokenSource();
        var late = gate.RequestAsync(WindowA, ReauthPurpose.FillPayment, cts2.Token);
        cts2.Cancel();
        fake.Pending.TrySetResult(ReauthOutcome.Verified);
        Assert.False(await late);
    }

    // Conta do Windows sem palavra-passe nem Hello: não pede (como o Firefox) e a página explica.
    [Fact]
    public async Task AccountWithoutProtection_DoesNotAsk_AndAdvises()
    {
        var fake = new FakeReauth(ReauthAvailability.Unavailable);
        var gate = Gate(fake);
        Assert.Null(gate.Advice);
        Assert.True(await gate.RequestAsync(WindowA, ReauthPurpose.Reveal));
        Assert.Equal(0, fake.Prompts);
        Assert.Equal(ReauthGate.ProtectAccountAdvice, gate.Advice);
        Assert.Contains("PIN", ReauthGate.ProtectAccountAdvice);
        // Descoberto ao pedir (palavra-passe em branco aceite pelo Windows): o mesmo.
        var blank = new FakeReauth { Outcome = ReauthOutcome.NoProtection };
        var gate2 = Gate(blank);
        Assert.True(await gate2.RequestAsync(WindowA, ReauthPurpose.Reveal));
        Assert.True(await gate2.RequestAsync(WindowA, ReauthPurpose.Export));
        Assert.Equal(1, blank.Prompts);
        Assert.Equal(ReauthGate.ProtectAccountAdvice, gate2.Advice);
    }

    // Dois pedidos ao mesmo tempo na mesma janela: um só pedido ao Windows.
    [Fact]
    public async Task ConcurrentRequests_AskOnce()
    {
        var fake = new FakeReauth { Pending = new() };
        var gate = Gate(fake);
        var a = gate.RequestAsync(WindowA, ReauthPurpose.Reveal);
        var b = gate.RequestAsync(WindowA, ReauthPurpose.Reveal);
        fake.Pending.TrySetResult(ReauthOutcome.Verified);
        Assert.True(await a);
        Assert.True(await b);
        Assert.Equal(1, fake.Prompts);
    }

    // D-5: preencher palavras-passe não pede por omissão; pagamentos e documentos pedem; mostrar e exportar pedem sempre.
    [Fact]
    public void Policy_FollowsTheOwnerDecisions()
    {
        Assert.False(ReauthPolicy.DefaultAskWhenFillingPasswords);
        Assert.True(ReauthPolicy.DefaultAskWhenFillingPayments);
        Assert.False(ReauthPolicy.Needs(ReauthPurpose.FillPassword, askPasswords: false, askPayments: true));
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.FillPassword, askPasswords: true, askPayments: true));
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.FillPayment, askPasswords: false, askPayments: true));
        Assert.False(ReauthPolicy.Needs(ReauthPurpose.FillPayment, askPasswords: false, askPayments: false));
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.FillDocument, askPasswords: false, askPayments: true));
        foreach (var always in new[] { ReauthPurpose.Reveal, ReauthPurpose.Export, ReauthPurpose.SyncSecrets, ReauthPurpose.ApproveDevice, ReauthPurpose.ChangePaymentSetting, ReauthPurpose.EraseVault })
            Assert.True(ReauthPolicy.Needs(always, askPasswords: false, askPayments: false), always.ToString());
        foreach (var purpose in Enum.GetValues<ReauthPurpose>())
        {
            var message = ReauthPolicy.Message(purpose);
            Assert.StartsWith("O Éteris quer ", message);
            Assert.EndsWith(".", message);
        }
    }

    // Os IIDs do UserConsentVerifier por COM puro (sem WinRT projetado): fixos, e o algoritmo dos IIDs parametrizados
    // confere com IIDs conhecidos do Windows.
    [Fact]
    public void Iids_AreFixed_AndTheParameterizedAlgorithmMatchesKnownOnes()
    {
        Assert.Equal(new Guid("cdb5efb3-5788-509d-9be1-71ccb8a3362a"), WinRtIid.Parameterized("pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};b1)"));
        Assert.Equal(new Guid("548cefbd-bc8a-5fa0-8df2-957440fc8bf4"), WinRtIid.Parameterized("pinterface({61c17706-2d65-11e0-9ae8-d48564015472};i4)"));
        Assert.Equal(new Guid("fd596ffd-2318-558f-9dbe-d21df43764a5"), UserConsent.IidAsyncVerificationResult);
        Assert.Equal(new Guid("ddd384f3-d818-5d83-ab4b-32119c28587c"), UserConsent.IidAsyncAvailability);
        Assert.Equal(WinRtIid.Parameterized("pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4))"), UserConsent.IidAsyncVerificationResult);
        Assert.Equal(WinRtIid.Parameterized("pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};enum(Windows.Security.Credentials.UI.UserConsentVerifierAvailability;i4))"), UserConsent.IidAsyncAvailability);
        Assert.Equal(new Guid("39E050C3-4E74-441A-8DC0-B81104DF949C"), UserConsent.IidInterop);
        Assert.Equal(new Guid("AF4F3F91-564C-4DDC-B8B5-973447627C65"), UserConsent.IidStatics);
        Assert.Equal(new Guid("00000036-0000-0000-C000-000000000046"), UserConsent.IidAsyncInfo);
    }

    [Fact]
    public void Results_MapToOutcomes()
    {
        Assert.Equal(ReauthOutcome.Verified, UserConsent.Map(0));
        Assert.Equal(ReauthOutcome.Canceled, UserConsent.Map(6));
        Assert.Equal(ReauthOutcome.Failed, UserConsent.Map(5));
        Assert.Equal(ReauthOutcome.Failed, UserConsent.Map(3));
        Assert.Null(UserConsent.Map(1));
        Assert.Null(UserConsent.Map(2));
        Assert.Equal(ReauthAvailability.Hello, UserConsent.MapAvailability(0));
        Assert.Equal(ReauthAvailability.WindowsPassword, UserConsent.MapAvailability(1));
        Assert.Equal(ReauthAvailability.WindowsPassword, UserConsent.MapAvailability(2));
        Assert.Equal(ReauthAvailability.WindowsPassword, UserConsent.MapAvailability(3));
        Assert.Equal(ReauthAvailability.WindowsPassword, UserConsent.MapAvailability(null));
    }

    // CredUI + LogonUser (sem Hello): até 3 tentativas; palavra-passe em branco recusada pela política do Windows
    // (ERROR_ACCOUNT_RESTRICTION) = conta sem palavra-passe, como o Chrome.
    [Fact]
    public void WindowsPassword_LogonErrors()
    {
        Assert.Equal(3, WindowsCredentialPrompt.MaxAttempts);
        Assert.Equal(WindowsCredentialPrompt.Check.NoProtection, WindowsCredentialPrompt.Classify(ok: false, error: 1327, blank: true));
        Assert.Equal(WindowsCredentialPrompt.Check.Wrong, WindowsCredentialPrompt.Classify(ok: false, error: 1327, blank: false));
        Assert.Equal(WindowsCredentialPrompt.Check.Wrong, WindowsCredentialPrompt.Classify(ok: false, error: 1326, blank: false));
        Assert.Equal(WindowsCredentialPrompt.Check.Verified, WindowsCredentialPrompt.Classify(ok: true, error: 0, blank: false));
        Assert.Equal(WindowsCredentialPrompt.Check.NoProtection, WindowsCredentialPrompt.Classify(ok: true, error: 0, blank: true));
        Assert.Equal(WindowsCredentialPrompt.Check.Failed, WindowsCredentialPrompt.Classify(ok: false, error: 1909, blank: false));
    }

    // A via real que não pede nada: a disponibilidade do Windows Hello neste computador (sem PIN, sem janela).
    [Fact]
    public void RealAvailability_AnswersWithoutAsking()
    {
        var started = DateTime.UtcNow;
        var availability = new WindowsReauth().CheckAvailability();
        Assert.True(Enum.IsDefined(availability));
        Assert.NotEqual(ReauthAvailability.Unavailable, availability);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    // ETERIS_TEST_REAUTH=aceitar|recusar: via de teste para as provas ao vivo, ignorada na versão instalada (como as
    // outras ETERIS_TEST_*).
    [Fact]
    public async Task TestSwitch_OnlyOutsideTheInstalledVersion()
    {
        Assert.Equal(TestReauthMode.Accept, TestSwitches.ReauthFor("aceitar", installed: false));
        Assert.Equal(TestReauthMode.Refuse, TestSwitches.ReauthFor(" Recusar ", installed: false));
        Assert.Null(TestSwitches.ReauthFor("aceitar", installed: true));
        Assert.Null(TestSwitches.ReauthFor("recusar", installed: true));
        Assert.Null(TestSwitches.ReauthFor("sim", installed: false));
        Assert.Null(TestSwitches.ReauthFor(null, installed: false));
        Assert.IsType<WindowsReauth>(Reauth.Create(null));
        var accept = Reauth.Create(TestReauthMode.Accept);
        Assert.Equal(ReauthOutcome.Verified, await accept.VerifyAsync(WindowA, "x", default));
        Assert.Equal(ReauthAvailability.Hello, accept.CheckAvailability());
        Assert.Equal(ReauthOutcome.Canceled, await Reauth.Create(TestReauthMode.Refuse).VerifyAsync(WindowA, "x", default));
        var motor = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "docs", "motor.md"));
        Assert.Contains("ETERIS_TEST_REAUTH", motor);
    }

    // Só APIs do sistema por P/Invoke e COM: nada de WinRT projetado nem pacotes novos.
    [Fact]
    public void RealReauth_UsesOnlySystemApis()
    {
        var root = GoogleSyncTests.FindRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "Vault", "WindowsReauth.cs"));
        Assert.Contains("combase.dll", source);
        Assert.Contains("credui.dll", source);
        Assert.DoesNotContain("using Windows.", source);
        var csproj = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        Assert.DoesNotContain("Microsoft.Windows.SDK", csproj);
        Assert.DoesNotContain("CsWinRT", csproj);
    }
}
