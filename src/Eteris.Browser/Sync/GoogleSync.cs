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
            return new("signedOut", _error ?? "Não ligado.", null);
        }
    }

    public static void BeginSignIn()
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
        _ = Task.Run(async () =>
        {
            try
            {
                var oauth = new GoogleOAuth(Http(), config, Now);
                var tokens = await oauth.SignInAsync(new SystemBrowser(), url => { lock (Gate) _manualUrl = url; }, cancel.Token);
                Session()?.Use(tokens);
                // Logo a seguir a entrar (ação do utilizador): com que conta, e a sincronização liga-se.
                _ = Accounts()?.AfterSignIn();
                SyncService.Start();
            }
            catch (OAuthException e) { lock (Gate) _error = e.Message; }
            catch (OperationCanceledException) { lock (Gate) _error = null; }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException)
            {
                lock (Gate) _error = "Sem ligação à Google. Tente de novo.";
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

    public static void CancelSignIn()
    {
        lock (Gate) _signIn?.Cancel();
    }

    public static async Task SignOutAsync()
    {
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
