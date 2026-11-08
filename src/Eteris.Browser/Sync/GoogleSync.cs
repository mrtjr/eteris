using System.IO;
using System.Net.Http;
using Eteris.Browser.Infrastructure;
using Eteris.Sync.Google;

namespace Eteris.Browser.Sync;

// «Entrar com o Google» (Definições › Sincronização, atrás da opção experimental desligada): início de sessão no
// navegador do sistema (PKCE + loopback), tokens guardados com DPAPI no perfil e revogados ao «Sair».
// Sem o identificador da aplicação (google-oauth.json, criado pelo dono na Google Cloud) fica «por configurar» e não
// faz pedido nenhum. Ligado, ainda não envia dados: a sincronização automática pelo Drive chega numa próxima fase.
internal static class GoogleSync
{
    private static readonly Lock Gate = new();
    private static GoogleSession? _session;
    private static HttpClient? _http;
    private static CancellationTokenSource? _signIn;
    private static string? _manualUrl;
    private static string? _error;

    public static string TokenPath => Path.Combine(AppPaths.Root, "sync", "google.tokens");

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

    public sealed record Status(string State, string Text, string? Url);

    public static Status Current()
    {
        if (Config is null)
            return new("unconfigured", "Por configurar: falta o identificador da aplicação Google (google-oauth.json). Nada é pedido à Google.", null);
        var signedIn = Session()?.IsSignedIn == true;
        lock (Gate)
        {
            if (_signIn is not null)
                return new("waiting", _manualUrl is null
                    ? "À espera do início de sessão no navegador do sistema…"
                    : "Não encontrámos outro navegador: copie este endereço para um navegador (Firefox, Chrome, Brave…) e entre lá.", _manualUrl);
            if (signedIn)
                return new("signedIn", "Ligado ao Google Drive (pasta oculta da aplicação). Ainda nada é enviado: a sincronização automática chega numa próxima versão.", null);
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
}
