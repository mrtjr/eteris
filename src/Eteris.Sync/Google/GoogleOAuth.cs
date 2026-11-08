using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eteris.Sync.Crypto;

namespace Eteris.Sync.Google;

// Identificador da aplicação na Google Cloud (tipo «App de computador»). Lê o JSON que a consola da Google descarrega
// ({"installed": {"client_id": …, "client_secret": …}}) ou a forma simples {"client_id": …}. Nas apps de computador o
// «client_secret» não é segredo (vai com o programa; a Google sabe-o) — quem protege a troca é o PKCE.
public sealed record OAuthClientConfig(string ClientId, string? ClientSecret)
{
    public static OAuthClientConfig? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("installed", out var installed)) root = installed;
            var id = root.TryGetProperty("client_id", out var c) ? c.GetString() : null;
            var secret = root.TryGetProperty("client_secret", out var s) ? s.GetString() : null;
            return string.IsNullOrWhiteSpace(id) || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
                ? null
                : new OAuthClientConfig(id.Trim(), string.IsNullOrWhiteSpace(secret) ? null : secret.Trim());
        }
        catch (JsonException) { return null; }
    }

    // O primeiro ficheiro que existir e for válido; null = «por configurar».
    public static OAuthClientConfig? Load(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path) && Parse(File.ReadAllText(path)) is { } config) return config;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}

public sealed record OAuthTokens(
    [property: JsonPropertyName("access")] string AccessToken,
    [property: JsonPropertyName("refresh")] string? RefreshToken,
    [property: JsonPropertyName("expires")] long ExpiresAtMs,
    [property: JsonPropertyName("scope")] string Scope);

public sealed class OAuthException(string message) : Exception(message);

// Abre um endereço no navegador do sistema (o Windows: ver Eteris.Browser/Sync/SystemBrowser.cs). Devolve false quando
// não há navegador que possa ser usado (por exemplo o predefinido é o próprio Éteris e não há outro): a interface
// mostra então o endereço para copiar.
public interface IExternalBrowser
{
    bool TryOpen(string url);
}

// PKCE (RFC 7636): o verificador fica no aparelho; à Google vai só o seu SHA-256.
public static class Pkce
{
    public static (string Verifier, string Challenge) Create()
    {
        var verifier = Kdf.Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier) => Kdf.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

public sealed record AuthorizationResponse(string? Code, string? State, string? Error);

// Recebe o redirecionamento da Google em http://127.0.0.1:<porta>/ (RFC 8252, «loopback»). Escuta só na interface de
// loopback (nada chega de fora, e o Windows não pede regra na firewall), aceita um único pedido GET e fecha.
public sealed class LoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener;

    private LoopbackReceiver(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://127.0.0.1:{Port}/";

    public static LoopbackReceiver Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        return new LoopbackReceiver(listener);
    }

    public async Task<AuthorizationResponse> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
            var parts = requestLine?.Split(' ');
            if (parts is not { Length: 3 } || parts[0] != "GET" || !parts[1].StartsWith("/?", StringComparison.Ordinal))
            {
                // O navegador pede também /favicon.ico: responde 404 e continua à espera do pedido certo.
                await RespondAsync(stream, 404, "Não encontrado", cancellationToken).ConfigureAwait(false);
                continue;
            }
            var query = ParseQuery(parts[1][2..]);
            var response = new AuthorizationResponse(query.GetValueOrDefault("code"), query.GetValueOrDefault("state"), query.GetValueOrDefault("error"));
            var ok = response.Code is not null;
            await RespondAsync(stream, 200, ok
                ? "Sessão iniciada no Éteris. Pode fechar esta aba e voltar ao Éteris."
                : "O Éteris não recebeu autorização. Pode fechar esta aba e tentar de novo no Éteris.", cancellationToken).ConfigureAwait(false);
            return response;
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Éteris</title>"
                   + "<body style=\"font:16px system-ui;margin:3em;background:#111;color:#eee\"><p>" + WebUtility.HtmlEncode(message) + "</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n"
                                           + $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(name, value);
        }
        return result;
    }

    public void Dispose() => _listener.Dispose();
}

// OAuth 2.0 para apps de computador (PKCE + loopback), com o navegador do sistema: a Google bloqueia o início de
// sessão dentro de motores embutidos como a CEF («disallowed_useragent»), e o Éteris também não o quer lá dentro.
public sealed class GoogleOAuth(HttpClient http, OAuthClientConfig config, Func<long> nowMs)
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    public string BuildAuthorizationUrl(string redirectUri, string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = GoogleEndpoints.Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            // Token de renovação (para sincronizar sem pedir sessão a cada arranque) e o ecrã sempre mostrado.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        };
        return GoogleEndpoints.Authorize + "?" + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    // Fluxo completo. «showUrl» é chamado quando não há navegador do sistema utilizável (mostrar para copiar).
    public async Task<OAuthTokens> SignInAsync(IExternalBrowser browser, Action<string>? showUrl, CancellationToken cancellationToken)
    {
        using var receiver = LoopbackReceiver.Start();
        var (verifier, challenge) = Pkce.Create();
        var state = Kdf.Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = BuildAuthorizationUrl(receiver.RedirectUri, challenge, state);
        if (!browser.TryOpen(url)) showUrl?.Invoke(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        AuthorizationResponse response;
        try { response = await receiver.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException("O início de sessão no navegador demorou demasiado. Tente de novo.");
        }
        if (response.Error is { } error)
            throw new OAuthException(error == "access_denied" ? "O acesso foi recusado no ecrã da Google." : "A Google devolveu um erro: " + error);
        if (response.State != state) throw new OAuthException("Resposta da Google não confere (estado diferente): ignorada.");
        if (response.Code is not { Length: > 0 } code) throw new OAuthException("A Google não devolveu código de autorização.");
        return await ExchangeCodeAsync(code, verifier, receiver.RedirectUri, cancellationToken).ConfigureAwait(false);
    }

    public Task<OAuthTokens> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
        => TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        }, previousRefresh: null, cancellationToken);

    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        => TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, refreshToken, cancellationToken);

    // «Sair»: a Google invalida o token (e o acesso da aplicação); o Éteris apaga a cópia local de qualquer forma.
    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        try
        {
            using var response = await http.PostAsync(GoogleEndpoints.Revoke, content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private async Task<OAuthTokens> TokenRequestAsync(Dictionary<string, string> form, string? previousRefresh, CancellationToken cancellationToken)
    {
        form["client_id"] = config.ClientId;
        if (config.ClientSecret is { } secret) form["client_secret"] = secret;
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(GoogleEndpoints.Token, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        TokenResponse? parsed;
        try { parsed = JsonSerializer.Deserialize<TokenResponse>(body); }
        catch (JsonException) { parsed = null; }
        if (!response.IsSuccessStatusCode || parsed?.AccessToken is not { Length: > 0 } access)
        {
            var error = parsed?.Error ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new OAuthException(error == "invalid_grant"
                ? "A autorização da Google expirou ou foi retirada. Entre outra vez."
                : "A Google recusou o pedido de sessão (" + error + ").");
        }
        var scope = parsed.Scope ?? string.Empty;
        // No ecrã da Google o utilizador pode desmarcar a permissão: sem ela não há sincronização.
        if (!scope.Split(' ').Contains(GoogleEndpoints.Scope))
            throw new OAuthException("Sem a permissão para a pasta da aplicação no Google Drive, o Éteris não consegue sincronizar.");
        return new OAuthTokens(access, parsed.RefreshToken ?? previousRefresh, nowMs() + Math.Max(60, parsed.ExpiresIn - 60) * 1000L, scope);
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("error")] string? Error);
}

// Proteção dos tokens no disco (no Windows: DPAPI do utilizador, Eteris.Browser/Sync/Dpapi.cs).
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

public sealed class TokenStore(string path, ISecretProtector protector)
{
    public OAuthTokens? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<OAuthTokens>(protector.Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or UnauthorizedAccessException) { return null; }
    }

    public void Save(OAuthTokens tokens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens)));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

// Sessão com a Google: devolve sempre um token de acesso válido, renovando-o quando expira.
public sealed class GoogleSession(GoogleOAuth oauth, TokenStore store, Func<long> nowMs)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthTokens? _tokens = store.Load();

    public bool IsSignedIn => _tokens is not null;

    public void Use(OAuthTokens tokens)
    {
        _tokens = tokens;
        store.Save(tokens);
    }

    public async Task<string> AccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tokens = _tokens ?? throw new OAuthException("Sem sessão iniciada na Google.");
            if (!forceRefresh && tokens.ExpiresAtMs > nowMs()) return tokens.AccessToken;
            if (tokens.RefreshToken is not { } refresh) throw new OAuthException("A sessão da Google expirou. Entre outra vez.");
            var renewed = await oauth.RefreshAsync(refresh, cancellationToken).ConfigureAwait(false);
            Use(renewed);
            return renewed.AccessToken;
        }
        finally { _gate.Release(); }
    }

    // Sair: revoga na Google (melhor esforço) e apaga os tokens deste aparelho.
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var tokens = _tokens;
        _tokens = null;
        store.Delete();
        if (tokens is not null) await oauth.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken, cancellationToken).ConfigureAwait(false);
    }
}
