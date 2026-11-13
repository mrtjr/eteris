using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Eteris.Browser.Vault;

// Reautenticação com o Windows (docs/palavras-passe.md, A3): Windows Hello (PIN, rosto, impressão digital) ou, sem
// Hello, a palavra-passe do Windows — antes de mostrar, copiar ou exportar segredos e de preencher pagamentos e
// documentos. É uma porta da interface, não uma cifra: um programa do mesmo utilizador lê a DPAPI sem ela.

public enum ReauthAvailability
{
    // Windows Hello configurado neste computador.
    Hello,
    // Sem Hello: pede a palavra-passe da conta do Windows (CredUI).
    WindowsPassword,
    // A conta não tem proteção nenhuma: não se pede nada (como o Firefox) e explica-se como a proteger.
    Unavailable,
}

public enum ReauthOutcome { Verified, Canceled, Failed, NoProtection }

public interface IReauth
{
    ReauthAvailability CheckAvailability();

    // owner: a janela do Éteris dona da janela do Windows (modal).
    Task<ReauthOutcome> VerifyAsync(nint owner, string message, CancellationToken cancel);
}

public enum ReauthPurpose
{
    // Mostrar, copiar ou editar uma palavra-passe; ver o número completo de um cartão, IBAN ou documento.
    Reveal,
    FillPassword,
    FillPayment,
    FillDocument,
    // Desligar «Pedir o Windows Hello ao preencher pagamentos» também pede.
    ChangePaymentSetting,
    // Desligar «Pedir o Windows Hello ao preencher palavras-passe» também pede.
    ChangePasswordSetting,
    EraseVault,
    // «Apagar todas as palavras-passe» (eteris://passwords): só as palavras-passe e os sites onde nunca guardar.
    ErasePasswords,
    // Sempre, sem janela de graça:
    Export,
    ExportProfileSecrets,
    SyncSecrets,
    ApproveDevice,
    // «Fui eu — juntar este computador» depois de a conta de sincronização ter sido substituída (com segredos ligados).
    JoinReplacedAccount,
    // «Ver o kit» de recuperação da sincronização (sempre: o kit aberto nunca dispensa a confirmação de «Ver»).
    ShowRecoveryKit,
    // «Perdi um computador / foi roubado»: remover o aparelho e retirar o acesso do Éteris à conta Google (sempre, haja
    // ou não segredos na conta).
    LostDevice,
    // «Começar do zero…»: apagar a conta de sincronização da nuvem e criar uma nova a partir deste computador (sempre).
    StartOver,
}

public static class ReauthPolicy
{
    // D-5: preencher palavras-passe não pede por omissão (como o Chrome e o Firefox); pagamentos e documentos pedem.
    public const bool DefaultAskWhenFillingPasswords = false;
    public const bool DefaultAskWhenFillingPayments = true;

    public static bool IsAlways(ReauthPurpose purpose)
        => purpose is ReauthPurpose.Export or ReauthPurpose.ExportProfileSecrets or ReauthPurpose.SyncSecrets or ReauthPurpose.ApproveDevice
            or ReauthPurpose.JoinReplacedAccount or ReauthPurpose.ShowRecoveryKit or ReauthPurpose.LostDevice
            or ReauthPurpose.StartOver;

    public static bool Needs(ReauthPurpose purpose, bool askPasswords, bool askPayments) => purpose switch
    {
        ReauthPurpose.FillPassword => askPasswords,
        ReauthPurpose.FillPayment or ReauthPurpose.FillDocument => askPayments,
        _ => true,
    };

    // O texto da janela do Windows (diz porquê).
    public static string Message(ReauthPurpose purpose) => purpose switch
    {
        ReauthPurpose.Reveal => "O Éteris quer mostrar um dado guardado no cofre.",
        ReauthPurpose.FillPassword => "O Éteris quer preencher uma palavra-passe.",
        ReauthPurpose.FillPayment => "O Éteris quer preencher um cartão ou um IBAN.",
        ReauthPurpose.FillDocument => "O Éteris quer preencher um documento de identificação.",
        ReauthPurpose.ChangePaymentSetting => "O Éteris quer deixar de pedir confirmação ao preencher pagamentos.",
        ReauthPurpose.ChangePasswordSetting => "O Éteris quer deixar de pedir confirmação ao preencher palavras-passe.",
        ReauthPurpose.EraseVault => "O Éteris quer apagar as palavras-passe e o preenchimento automático.",
        ReauthPurpose.ErasePasswords => "O Éteris quer apagar todas as palavras-passe guardadas.",
        ReauthPurpose.Export => "O Éteris quer exportar as suas palavras-passe para um ficheiro.",
        ReauthPurpose.ExportProfileSecrets => "O Éteris quer pôr as palavras-passe, os pagamentos e os documentos no perfil exportado.",
        ReauthPurpose.SyncSecrets => "O Éteris quer sincronizar pagamentos ou documentos com os seus outros computadores.",
        ReauthPurpose.ApproveDevice => "O Éteris quer dar as suas palavras-passe a outro computador.",
        // PROPOSTA (P3).
        ReauthPurpose.JoinReplacedAccount => "O Éteris quer juntar este computador a outra conta de sincronização.",
        ReauthPurpose.ShowRecoveryKit => "O Éteris quer mostrar o seu kit de recuperação.",
        // PROPOSTA (T12).
        ReauthPurpose.LostDevice => "O Éteris quer remover um computador da sua conta e retirar o acesso do Éteris à conta Google.",
        ReauthPurpose.StartOver => "O Éteris quer apagar a conta de sincronização na nuvem e criar uma nova a partir deste computador.",
        _ => "O Éteris quer confirmar que é você.",
    };
}

// A janela de graça: 5 minutos por janela do Éteris, como o Firefox, contados desde a verificação (não se renova a
// cada uso). Recomeça quando o Windows bloqueia, o computador suspende ou a janela fecha. Os pedidos «sempre» nem a
// usam nem a criam. Um pedido cancelado (a aba saiu da frente) não conta, mesmo que o Windows responda depois.
public sealed class ReauthGate
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    public const string ProtectAccountAdvice =
        "A sua conta do Windows não tem palavra-passe nem Windows Hello: o Éteris não pode confirmar que é você. Proteja a sua conta do Windows com um PIN para proteger as palavras-passe.";

    private readonly IReauth _reauth;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<nint, DateTimeOffset> _verified = [];
    private readonly SemaphoreSlim _one = new(1, 1);
    // Quando se viu que a conta do Windows não tem proteção (null: não se viu). Vale a janela de graça: depois (ou ao
    // bloquear), volta-se a verificar, para quem seguiu o conselho e pôs um PIN não ficar sem confirmação até reiniciar.
    private DateTimeOffset? _unprotectedAt;

    private bool Unprotected => _unprotectedAt is { } at && _clock() - at < Grace;

    public ReauthGate(IReauth reauth, Func<DateTimeOffset>? clock = null)
    {
        _reauth = reauth;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    // A do navegador: criada só quando é precisa, presa aos eventos do Windows (bloqueio, suspensão).
    private static readonly Lazy<ReauthGate> s_shared = new(() =>
    {
        // A via de teste (ETERIS_TEST_REAUTH) só vale num perfil de teste (--user-data-dir): contra o perfil de omissão a
        // porta é sempre a real.
        var gate = new ReauthGate(Reauth.Create(Infrastructure.TestSwitches.ReauthForProfile(Infrastructure.TestSwitches.Reauth, Infrastructure.AppPaths.IsCustomProfile)));
        SystemEvents.SessionSwitch += (_, e) => { if (ResetsOn(e.Reason)) gate.Reset(); };
        SystemEvents.PowerModeChanged += (_, e) => { if (ResetsOn(e.Mode)) gate.Reset(); };
        return gate;
    });

    public static ReauthGate Shared => s_shared.Value;

    // Uma janela fechou: a graça dela acaba (sem criar a porta só para isso).
    public static void ForgetWindowIfCreated(nint window)
    {
        if (s_shared.IsValueCreated) s_shared.Value.WindowClosed(window);
    }

    // Texto para a página quando a conta do Windows não tem proteção (null se tem).
    public string? Advice => _unprotectedAt is not null ? ProtectAccountAdvice : null;

    public static bool ResetsOn(SessionSwitchReason reason)
        => reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff;

    public static bool ResetsOn(PowerModes mode) => mode == PowerModes.Suspend;

    public bool InGrace(nint window)
    {
        lock (_verified) return _verified.TryGetValue(window, out var at) && _clock() - at < Grace;
    }

    public void WindowClosed(nint window)
    {
        lock (_verified) _verified.Remove(window);
    }

    public void Reset()
    {
        lock (_verified) _verified.Clear();
        _unprotectedAt = null;
    }

    public async Task<bool> RequestAsync(nint window, ReauthPurpose purpose, CancellationToken cancel = default)
    {
        var always = ReauthPolicy.IsAlways(purpose);
        if (!always && InGrace(window)) return true;
        try { await _one.WaitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        try
        {
            if (cancel.IsCancellationRequested) return false;
            if (!always && InGrace(window)) return true;
            if (Unprotected) return true;
            if (_reauth.CheckAvailability() == ReauthAvailability.Unavailable)
            {
                _unprotectedAt = _clock();
                return true;
            }
            _unprotectedAt = null;
            ReauthOutcome outcome;
            try { outcome = await _reauth.VerifyAsync(window, ReauthPolicy.Message(purpose), cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            if (cancel.IsCancellationRequested) return false;
            switch (outcome)
            {
                case ReauthOutcome.Verified:
                    if (!always) lock (_verified) _verified[window] = _clock();
                    return true;
                case ReauthOutcome.NoProtection:
                    _unprotectedAt = _clock();
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _one.Release();
        }
    }
}

public enum TestReauthMode { Accept, Refuse }

public static class Reauth
{
    // A real, ou a de teste (ETERIS_TEST_REAUTH, nunca na versão instalada: Infrastructure.TestSwitches.Reauth). A de teste só
    // existe numa compilação com a constante ETERIS_TEST_HOOKS: a publicação não a leva (build/publish.ps1).
#if ETERIS_TEST_HOOKS
    public static IReauth Create(TestReauthMode? test) => test is { } mode ? new TestReauth(mode) : new WindowsReauth();
#else
    public static IReauth Create(TestReauthMode? test) => new WindowsReauth();
#endif
}

#if ETERIS_TEST_HOOKS
// Só para as provas ao vivo: responde sem janela nenhuma.
internal sealed class TestReauth(TestReauthMode mode) : IReauth
{
    public ReauthAvailability CheckAvailability() => ReauthAvailability.Hello;

    public Task<ReauthOutcome> VerifyAsync(nint owner, string message, CancellationToken cancel)
        => Task.FromResult(mode == TestReauthMode.Accept ? ReauthOutcome.Verified : ReauthOutcome.Canceled);
}
#endif

// A real: Windows Hello pelo UserConsentVerifier (COM puro) e, sem Hello, a palavra-passe do Windows (CredUI).
public sealed class WindowsReauth : IReauth
{
    public ReauthAvailability CheckAvailability() => UserConsent.MapAvailability(UserConsent.CheckAvailability());

    public Task<ReauthOutcome> VerifyAsync(nint owner, string message, CancellationToken cancel)
    {
        var done = new TaskCompletionSource<ReauthOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var hello = UserConsent.MapAvailability(UserConsent.CheckAvailability()) == ReauthAvailability.Hello
                    ? UserConsent.Map(UserConsent.RequestVerification(owner, message, cancel))
                    : null;
                done.TrySetResult(hello ?? WindowsCredentialPrompt.Verify(owner, message, cancel));
            }
            catch (Exception)
            {
                // Nada pode derrubar o Éteris a partir desta thread: uma falha do Windows é «não confirmado».
                done.TrySetResult(ReauthOutcome.Failed);
            }
        }) { IsBackground = true, Name = "Éteris: confirmar com o Windows" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task;
    }
}

// Os IIDs dos tipos parametrizados do WinRT (o algoritmo «pinterface»: SHA-1 do espaço de nomes
// 11f47ad5-7b73-42c0-abae-878b1e16adee e da assinatura, versão 5).
public static class WinRtIid
{
    private static readonly Guid Namespace = new("11f47ad5-7b73-42c0-abae-878b1e16adee");

    public static Guid Parameterized(string signature)
    {
        var data = Namespace.ToByteArray(bigEndian: true).Concat(Encoding.UTF8.GetBytes(signature)).ToArray();
        var hash = SHA1.HashData(data).AsSpan(0, 16).ToArray();
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }
}

// Windows.Security.Credentials.UI.UserConsentVerifier por COM puro (combase.dll), o caminho do Chrome
// (chrome/browser/device_reauth/win/authenticator_win.cc): IUserConsentVerifierInterop::RequestVerificationForWindowAsync
// com a janela do Éteris como dona. A operação assíncrona é esperada consultando IAsyncInfo::get_Status nesta thread.
public static unsafe class UserConsent
{
    private const string ClassName = "Windows.Security.Credentials.UI.UserConsentVerifier";

    public static readonly Guid IidInterop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    public static readonly Guid IidStatics = new("AF4F3F91-564C-4DDC-B8B5-973447627C65");
    public static readonly Guid IidAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    // IAsyncOperation<UserConsentVerificationResult> e IAsyncOperation<UserConsentVerifierAvailability> (WinRtIid).
    public static readonly Guid IidAsyncVerificationResult = new("fd596ffd-2318-558f-9dbe-d21df43764a5");
    public static readonly Guid IidAsyncAvailability = new("ddd384f3-d818-5d83-ab4b-32119c28587c");

    // UserConsentVerificationResult: Verified 0, DeviceNotPresent 1, NotConfiguredForUser 2, DisabledByPolicy 3,
    // DeviceBusy 4, RetriesExhausted 5, Canceled 6. null = sem Hello: passa à palavra-passe do Windows.
    public static ReauthOutcome? Map(int? result) => result switch
    {
        0 => ReauthOutcome.Verified,
        6 => ReauthOutcome.Canceled,
        1 or 2 => null,
        null => ReauthOutcome.Canceled,
        _ => ReauthOutcome.Failed,
    };

    // UserConsentVerifierAvailability: Available 0; o resto (sem aparelho, não configurado, política, ocupado) passa à
    // palavra-passe do Windows. Nunca «Unavailable»: só se sabe que a conta não tem palavra-passe ao pedi-la.
    public static ReauthAvailability MapAvailability(int? availability) => availability == 0 ? ReauthAvailability.Hello : ReauthAvailability.WindowsPassword;

    public static int? CheckAvailability()
    {
        try
        {
            return OnMta(() =>
            {
                var statics = Factory(IidStatics);
                try
                {
                    nint operation;
                    var check = (delegate* unmanaged<nint, nint*, int>)(*(nint**)statics)[6];
                    Marshal.ThrowExceptionForHR(check(statics, &operation));
                    try { return Await(operation, CancellationToken.None); }
                    finally { Release(operation); }
                }
                finally { Release(statics); }
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static int? RequestVerification(nint owner, string message, CancellationToken cancel) => OnMta(() => Request(owner, message, cancel));

    private static int? Request(nint owner, string message, CancellationToken cancel)
    {
        var interop = Factory(IidInterop);
        var text = HString(message);
        try
        {
            nint operation;
            var iid = IidAsyncVerificationResult;
            var request = (delegate* unmanaged<nint, nint, nint, Guid*, nint*, int>)(*(nint**)interop)[6];
            Marshal.ThrowExceptionForHR(request(interop, owner, text, &iid, &operation));
            try { return Await(operation, cancel); }
            finally { Release(operation); }
        }
        finally
        {
            WindowsDeleteString(text);
            Release(interop);
        }
    }

    // IAsyncInfo: get_Status (7), get_ErrorCode (8), Cancel (9); IAsyncOperation<enum>: GetResults (8).
    private static int? Await(nint operation, CancellationToken cancel)
    {
        nint info;
        var iid = IidAsyncInfo;
        var query = (delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)operation)[0];
        Marshal.ThrowExceptionForHR(query(operation, &iid, &info));
        try
        {
            var status = (delegate* unmanaged<nint, int*, int>)(*(nint**)info)[7];
            var cancelCall = (delegate* unmanaged<nint, int>)(*(nint**)info)[9];
            int state;
            var askedToCancel = false;
            while (true)
            {
                Marshal.ThrowExceptionForHR(status(info, &state));
                if (state != 0) break;
                if (cancel.IsCancellationRequested && !askedToCancel)
                {
                    cancelCall(info);
                    askedToCancel = true;
                }
                Thread.Sleep(50);
            }
            if (state == 2) return null;
            if (state == 3)
            {
                int error;
                var errorCode = (delegate* unmanaged<nint, int*, int>)(*(nint**)info)[8];
                errorCode(info, &error);
                Marshal.ThrowExceptionForHR(error);
                return null;
            }
            int result;
            var results = (delegate* unmanaged<nint, int*, int>)(*(nint**)operation)[8];
            Marshal.ThrowExceptionForHR(results(operation, &result));
            return result;
        }
        finally { Release(info); }
    }

    private static nint Factory(Guid iid)
    {
        var name = HString(ClassName);
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, &iid, out var factory));
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    private static nint HString(string text)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(text, (uint)text.Length, out var hstring));
        return hstring;
    }

    private static void Release(nint unknown)
    {
        if (unknown == 0) return;
        ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);
    }

    // O WinRT precisa da thread inicializada; numa thread nossa, MTA.
    private static T OnMta<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA) return Init(work);
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = Init(work); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return result;
    }

    private static T Init<T>(Func<T> work)
    {
        var hr = RoInitialize(1);
        try { return work(); }
        finally { if (hr >= 0) RoUninitialize(); }
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, out nint factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string source, uint length, out nint hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoInitialize(int type);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern void RoUninitialize();
}

// Sem Windows Hello: a palavra-passe da conta do Windows (credui.dll + advapi32.dll), o caminho do Chrome
// (chrome/browser/password_manager/password_manager_util_win.cc): CredUIPromptForWindowsCredentials com
// CREDUIWIN_ENUMERATE_CURRENT_USER, CredUnPackAuthenticationBuffer e LogonUser(LOGON32_LOGON_NETWORK), até 3 tentativas.
// O buffer da palavra-passe é apagado logo a seguir. Uma palavra-passe em branco que o Windows recusa por política
// (ERROR_ACCOUNT_RESTRICTION) é uma conta sem palavra-passe: conta como «sem proteção», como no Chrome.
public static unsafe class WindowsCredentialPrompt
{
    public const int MaxAttempts = 3;

    public enum Check { Verified, Wrong, NoProtection, Failed }

    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorCancelled = 1223;
    private const int EnumerateCurrentUser = 0x200;
    private const int PackProtectedCredentials = 0x1;
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    public static Check Classify(bool ok, int error, bool blank) => (ok, error, blank) switch
    {
        (true, _, true) => Check.NoProtection,
        (true, _, false) => Check.Verified,
        (false, ErrorAccountRestriction, true) => Check.NoProtection,
        (false, ErrorLogonFailure or ErrorAccountRestriction, _) => Check.Wrong,
        _ => Check.Failed,
    };

    public static ReauthOutcome Verify(nint owner, string message, CancellationToken cancel)
    {
        var lastError = 0;
        const int size = 514;
        var user = stackalloc char[size];
        var domain = stackalloc char[size];
        var password = stackalloc char[size];
        for (var attempt = 0; attempt < MaxAttempts && !cancel.IsCancellationRequested; attempt++)
        {
            var info = new CredUiInfo { Size = Marshal.SizeOf<CredUiInfo>(), Parent = owner, Message = message, Caption = "Éteris" };
            uint package = 0, outSize = 0;
            nint outBuffer = 0;
            var save = false;
            var result = CredUIPromptForWindowsCredentialsW(ref info, lastError, ref package, 0, 0, out outBuffer, out outSize, ref save, EnumerateCurrentUser);
            if (result == ErrorCancelled) return ReauthOutcome.Canceled;
            if (result != 0) return ReauthOutcome.Failed;
            var check = Check.Failed;
            try
            {
                int userLength = size, domainLength = size, passwordLength = size;
                if (CredUnPackAuthenticationBufferW(PackProtectedCredentials, outBuffer, outSize, user, ref userLength, domain, ref domainLength, password, ref passwordLength))
                {
                    var name = new string(user);
                    var realm = new string(domain);
                    if (realm.Length == 0 && name.Contains('\\'))
                    {
                        realm = name[..name.IndexOf('\\')];
                        name = name[(name.IndexOf('\\') + 1)..];
                    }
                    var blank = password[0] == '\0';
                    var ok = LogonUserW(name, realm.Length == 0 ? null : realm, password, Logon32LogonNetwork, Logon32ProviderDefault, out var token);
                    var error = ok ? 0 : Marshal.GetLastPInvokeError();
                    if (ok) CloseHandle(token);
                    check = Classify(ok, error, blank);
                }
            }
            finally
            {
                new Span<char>(password, size).Clear();
                new Span<char>(user, size).Clear();
                new Span<char>(domain, size).Clear();
                if (outBuffer != 0)
                {
                    new Span<byte>((void*)outBuffer, (int)outSize).Clear();
                    CoTaskMemFree(outBuffer);
                }
            }
            switch (check)
            {
                case Check.Verified: return ReauthOutcome.Verified;
                case Check.NoProtection: return ReauthOutcome.NoProtection;
                case Check.Wrong: lastError = ErrorLogonFailure; continue;
                default: return ReauthOutcome.Failed;
            }
        }
        return cancel.IsCancellationRequested ? ReauthOutcome.Canceled : ReauthOutcome.Failed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int Size;
        public nint Parent;
        public string Message;
        public string Caption;
        public nint Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentialsW(ref CredUiInfo info, int authError, ref uint authPackage, nint inBuffer, uint inSize,
        out nint outBuffer, out uint outSize, ref bool save, int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBufferW(int flags, nint buffer, uint size, char* user, ref int userLength, char* domain,
        ref int domainLength, char* password, ref int passwordLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUserW(string user, string? domain, char* password, int logonType, int provider, out nint token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint memory);
}
