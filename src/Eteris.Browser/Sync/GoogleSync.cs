using System.IO;
using System.Net.Http;
using Eteris.Browser.Infrastructure;
using Eteris.Sync.Google;

namespace Eteris.Browser.Sync;

// «Entrar com o Google» (Definições › Sincronização e menu da conta): início de sessão no navegador do sistema
// (PKCE + loopback), tokens guardados com DPAPI no perfil e revogados ao «Sair». Sem o identificador da aplicação
// (google-oauth.json, criado pelo dono na Google Cloud e copiado pelo build/publish.ps1) fica «indisponível nesta
// compilação» e não faz pedido nenhum. Depois de entrar, a sincronização (SyncService) liga-se e usa o Drive
// (DriveTransport: só a pasta oculta da aplicação).
// A conta (nome e e-mail, para o menu e as Definições) pede-se ao Drive logo a seguir a entrar, ou — se já havia sessão
// sem conta guardada — quando o utilizador abre o menu ou a secção Sincronização (RequestAccount). Nunca no arranque.
internal static class GoogleSync
{
    private static readonly Lock Gate = new();
    private static GoogleSession? _session;
    private static HttpClient? _http;
    private static CancellationTokenSource? _signIn;
    private static string? _manualUrl;
    private static string? _error;
    private static GoogleAccountService? _account;

    // A conta mudou (obtida ou apagada ao sair). Chamado de qualquer thread.
    public static event Action? AccountChanged;

    // O estado tipado do início de sessão, para a barra da janela (0.2.15). Só memória: ler Progress não toca no disco
    // nem cria nada (a barra pergunta-o a cada evento, também com tudo parado).
    private static readonly SignInTracker Tracker = NewTracker();

    // O estado do início de sessão mudou. Chamado de qualquer thread.
    public static event Action? SignInChanged;

    internal static SignInProgress Progress => Tracker.Current;

    // O «some sozinha» ou o «Fechar» da barra: limpa o estado terminal com este AtMs (e só esse).
    internal static void ClearSignIn(long atMs) => Tracker.Clear(atMs);

    // «Tentar outra vez»: com o mesmo e-mail da reentrada, se havia.
    internal static void RetrySignIn() => BeginSignIn(Tracker.LastHint);

    // Esquece o estado do início de sessão e o e-mail da reentrada (sair da conta; retirar o acesso do Éteris).
    internal static void ResetSignIn() => Tracker.Reset();

    private static SignInTracker NewTracker()
    {
        var tracker = new SignInTracker(Now);
        tracker.Changed += () => SignInChanged?.Invoke();
        return tracker;
    }

    public static string TokenPath => Path.Combine(AppPaths.Root, "sync", "google.tokens");

    public static string AccountPath => Path.Combine(AppPaths.Root, "sync", "google.account");

    // Onde se procura o identificador: ao lado do programa (vai com a instalação) ou no perfil (testes do dono).
    public static IEnumerable<string> ConfigPaths =>
    [
        Path.Combine(AppContext.BaseDirectory, "google-oauth.json"),
        Path.Combine(AppPaths.Root, "google-oauth.json"),
    ];

    public static OAuthClientConfig? Config => OAuthClientConfig.Load(ConfigPaths);

    public static bool SigningIn
    {
        get { lock (Gate) return _signIn is not null; }
    }

    public sealed record Status(string State, string Text, string? Url, GoogleAccount? Account = null, bool AccountPending = false);

    // Só disco (tokens e conta cifrados): nenhum pedido à Google.
    public static bool SignedIn => Session()?.IsSignedIn == true;

    public static GoogleAccount? Account => SignedIn ? Accounts()?.Current : null;

    // O e-mail da conta guardada, só quando há tokens no disco (reentrada); senão null. Ler ANTES de BeginSignIn: a entrada
    // esquece a conta antiga.
    internal static string? ReentryHint() => SignedIn ? Account?.Email : null;

    public static bool AccountPending => Accounts()?.Pending == true;

    // Uma ação do utilizador (abrir o menu, abrir a secção Sincronização) quer mostrar a conta: se há sessão e nenhuma
    // conta guardada, pede-a em segundo plano (no máximo uma tentativa falhada por sessão). Devolve true se pediu.
    public static bool RequestAccount() => Accounts()?.RequestIfMissing(SignedIn) is not null;

    // O texto do estado com sessão: com a conta, «Ligado como Nome (e-mail)…».
    internal static string SignedInText(GoogleAccount? account)
        => (account is { IsEmpty: false } ? $"Ligado como {account.Label} ao Google Drive" : "Ligado ao Google Drive")
           + " (pasta oculta da aplicação). Tudo vai cifrado neste computador: a Google só vê ficheiros cifrados, os tamanhos e as horas.";

    public const string UnavailableText = "Indisponível nesta compilação: falta o identificador da aplicação Google (google-oauth.json). Nada é pedido à Google.";

    // O transporte da sincronização: a pasta oculta da aplicação no Drive desta sessão (null sem sessão).
    internal static Eteris.Sync.Transport.ISyncTransport? DriveTransport()
        => Session() is { IsSignedIn: true } session ? new GoogleDriveTransport(Http(), session.AccessTokenAsync) : null;

    public static Status Current()
    {
        if (Config is null)
            return new("unconfigured", UnavailableText, null);
        var signedIn = Session()?.IsSignedIn == true;
        var accounts = signedIn ? Accounts() : null;
        lock (Gate)
        {
            if (_signIn is not null)
                return new("waiting", _manualUrl is null
                    ? "À espera do início de sessão no navegador do sistema…"
                    : "Não encontrámos outro navegador: copie este endereço para um navegador (Firefox, Chrome, Brave…) e entre lá.", _manualUrl);
            if (signedIn)
            {
                var account = accounts?.Current;
                return new("signedIn", SignedInText(account), null, account, accounts?.Pending == true);
            }
            return new("signedOut", _error ?? "Não tem sessão iniciada na Google.", null);
        }
    }

    // «loginHint»: o e-mail da conta guardada neste computador, só na reentrada (ver ReentryHint). Vai em memória, nunca
    // por um endereço «eteris://».
    public static void BeginSignIn(string? loginHint = null)
    {
        if (Config is not { } config) return;
        CancellationTokenSource cancel;
        lock (Gate)
        {
            if (_signIn is not null) return;
            _signIn = cancel = new CancellationTokenSource();
            _manualUrl = null;
            _error = null;
        }
        Tracker.Begin(loginHint);
        _ = Task.Run(async () =>
        {
            // O trabalho depois dos tokens (guardá-los, a conta, ligar a sincronização) não se cancela a meio: corre até
            // ao fim mesmo que a página já tenha saído com o texto neutro.
            Task<SignInPageKind>? after = null;
            try
            {
                var oauth = new GoogleOAuth(Http(), config, Now);
                var options = new SignInOptions(new SystemBrowser(), url =>
                {
                    lock (Gate) _manualUrl = url;
                    Tracker.ShowManualUrl(url);
                }, loginHint)
                {
                    // «Cancelar» antes de os tokens chegarem a ser usados: não se guarda nada.
                    Finish = (tokens, ct) => ct.IsCancellationRequested
                        ? Task.FromCanceled<SignInPageKind>(ct)
                        : (after = AfterTokensAsync(tokens)).WaitAsync(ct),
                    // Uma vez, quando a sessão fica iniciada (nunca numa recusa): pisca a janela do Éteris.
                    SignedIn = MainWindow.SignInArrived,
                };
                try { await oauth.SignInAsync(options, cancel.Token); }
                finally
                {
                    try
                    {
                        if (after is not null) await after;
                    }
                    finally
                    {
                        // «Cancelar» quando os tokens já estavam a ser usados: este computador fica sem sessão.
                        if (after is not null && cancel.IsCancellationRequested) UndoSignIn();
                    }
                }
            }
            catch (OAuthException e)
            {
                lock (Gate) _error = e.Message;
                Tracker.Failed(e);
            }
            catch (OperationCanceledException e)
            {
                lock (Gate) _error = null;
                Tracker.Failed(e);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException)
            {
                lock (Gate) _error = "Sem ligação à Google. Tente de novo.";
                Tracker.Failed(e);
            }
            catch (Exception e)
            {
                // Inesperado: a barra não fica presa em «À espera…»; a exceção segue como antes.
                Tracker.Failed(e);
                throw;
            }
            finally
            {
                lock (Gate)
                {
                    _signIn = null;
                    _manualUrl = null;
                }
                cancel.Dispose();
            }
        });
    }

    // Com os tokens na mão (a página do navegador à espera): guarda-os, pede a conta — antes de ligar a sincronização,
    // para ela saber se a sessão é da mesma conta Google de antes (com outra, fica em pausa sem apagar nada) — e liga a
    // sincronização. Devolve a página de fim que o estado da sincronização justifica.
    private static async Task<SignInPageKind> AfterTokensAsync(OAuthTokens tokens)
    {
        var hadKey = SyncService.Agent()?.HasKey == true; // antes de ligar
        Session()?.Use(tokens);
        if (Accounts()?.AfterSignIn() is { } account) await account;
        // Com a conta já pedida (a barra diz «Sessão iniciada como Nome») e antes de ligar a sincronização.
        Tracker.SignedIn();
        var round = SyncService.StartAsync();
        var kind = await SignInFinish.WaitAsync(() => SyncService.Agent()?.Status, round, hadKey, SignInFinish.Poll, CancellationToken.None);
        Tracker.Finished(kind);
        return kind;
    }

    // O utilizador cancelou quando os tokens já tinham chegado: este computador fica sem sessão. Só local — revogar na
    // Google desligava os outros computadores da pessoa. Uma conta de sincronização já criada no Drive fica lá.
    private static void UndoSignIn()
    {
        SyncService.SignOut(forgetKey: false);
        Accounts()?.Forget();
        Session()?.SignOut();
        // A barra não pode continuar a dizer «Sessão iniciada» (a entrada pode ter acabado sem exceção).
        Tracker.Reset();
    }

    public static void CancelSignIn()
    {
        lock (Gate) _signIn?.Cancel();
    }

    public static async Task SignOutAsync()
    {
        Tracker.Reset();
        var session = Session();
        if (session is null) return;
        Accounts()?.Forget();
        try { await session.SignOutAsync(CancellationToken.None); }
        catch (Exception e) when (e is HttpRequestException or IOException) { }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static HttpClient Http()
    {
        lock (Gate) return _http ??= GoogleEndpoints.CreateClient();
    }

    private static GoogleSession? Session()
    {
        if (Config is not { } config) return null;
        var http = Http();
        lock (Gate) return _session ??= new GoogleSession(new GoogleOAuth(http, config, Now), new TokenStore(TokenPath, new Dpapi("google-tokens")), Now);
    }

    private static GoogleAccountService? Accounts()
    {
        if (Session() is not { } session) return null;
        var http = Http();
        lock (Gate)
        {
            if (_account is not null) return _account;
            _account = new GoogleAccountService(http, session.AccessTokenAsync, new GoogleAccountStore(AccountPath, new Dpapi("google-account")));
            _account.Changed += _ => AccountChanged?.Invoke();
            return _account;
        }
    }
}
