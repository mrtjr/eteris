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

// Porque falhou uma entrada, para quem tem de escolher o que mostrar (a página de fim, a barra).
public enum OAuthFailure
{
    Other,
    TimedOut,     // ninguém respondeu no navegador dentro do prazo
    Refused,      // o ecrã da Google devolveu um erro (a pessoa recusou, por exemplo)
    MissingScope, // a permissão da pasta do Drive foi desmarcada
}

// NeedsSignIn: a autorização foi retirada ou expirou (invalid_grant, sem token de renovação, permissão desmarcada) —
// não passa sozinha; é preciso entrar outra vez.
public sealed class OAuthException(string message, bool needsSignIn = false, OAuthFailure failure = OAuthFailure.Other) : Exception(message)
{
    public bool NeedsSignIn { get; } = needsSignIn;

    public OAuthFailure Failure { get; } = failure;
}

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
// loopback (nada chega de fora, e o Windows não pede regra na firewall). O pedido certo não leva resposta logo: a
// ligação fica guardada (o navegador mostra «a carregar») até quem chama saber o que dizer — CompleteAsync com a página
// de fim, ou RedirectAsync para o ecrã da Google (a 2.ª volta, no mesmo separador).
public sealed class LoopbackReceiver : IDisposable
{
    // As escritas não podem prender a entrada: o utilizador pode ter fechado o separador.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    // A política deixa passar só o <style> e o SVG embutidos (o SVG não é um carregamento): nada de fora, nada de scripts.
    internal const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private readonly TcpListener _listener;
    private readonly Lock _gate = new();
    private TcpClient? _held;
    private bool _stopped;

    private LoopbackReceiver(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://127.0.0.1:{Port}/";

    // Quanto se espera pela linha do pedido de cada ligação: uma ligação local que não envie nada não prende o recetor
    // até ao prazo da entrada (a fila tem um só lugar). Passado o prazo, fecha-se sem resposta e espera-se pela seguinte.
    internal TimeSpan RequestLineTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public static LoopbackReceiver Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        return new LoopbackReceiver(listener);
    }

    // Só a resposta com o «state» deste início de sessão conta (comparação em tempo constante). Qualquer outra — uma
    // página local ou um processo que varra as portas e mande «/?error=…» ou um código seu — leva 400 e o recetor
    // continua à espera até ao prazo: não aborta o início de sessão nem põe texto alheio na interface.
    public async Task<AuthorizationResponse> WaitAsync(string expectedState, CancellationToken cancellationToken)
    {
        var expected = Encoding.UTF8.GetBytes(expectedState);
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var keep = false;
            try
            {
                var stream = client.GetStream();
                string? requestLine;
                using (var line = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    line.CancelAfter(RequestLineTimeout);
                    try { requestLine = await ReadRequestLineAsync(stream, line.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { continue; } // muda: fecha-se sem resposta
                    catch (IOException) { continue; }
                }
                var parts = requestLine?.Split(' ');
                if (parts is not { Length: 3 } || parts[0] != "GET" || !parts[1].StartsWith("/?", StringComparison.Ordinal))
                {
                    // O navegador pede também /favicon.ico: responde 404 e continua à espera do pedido certo.
                    await WriteAsync(stream, 404, null, MessagePage("Não encontrado"), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var query = ParseQuery(parts[1][2..]);
                var response = new AuthorizationResponse(query.GetValueOrDefault("code"), query.GetValueOrDefault("state"), query.GetValueOrDefault("error"));
                if (response.State is not { } state || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), expected))
                {
                    await WriteAsync(stream, 400, null, MessagePage("Pedido não reconhecido pelo Éteris."), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // O pedido certo: a resposta espera por CompleteAsync ou RedirectAsync.
                TcpClient? previous;
                lock (_gate)
                {
                    previous = _held;
                    _held = client;
                }
                previous?.Dispose();
                keep = true;
                return response;
            }
            finally
            {
                if (!keep) client.Dispose();
            }
        }
    }

    // A página de fim, na ligação guardada (que depois se fecha). Sem ligação guardada, não faz nada.
    public async Task CompleteAsync(SignInPageKind page, CancellationToken cancellationToken)
    {
        if (Take() is not { } client) return;
        using (client)
            await WriteAsync(client.GetStream(), 200, null, SignInPage.Html(page), cancellationToken).ConfigureAwait(false);
    }

    // Manda o separador que está à espera para o ecrã da Google (302), e só para lá.
    public async Task RedirectAsync(string url, CancellationToken cancellationToken)
    {
        if (url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !GoogleEndpoints.IsAllowedBrowserUrl(uri))
            throw new InvalidOperationException("O recetor só redireciona para o ecrã de autorização da Google.");
        if (Take() is not { } client) return;
        using (client)
            await WriteAsync(client.GetStream(), 302, url, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    // Deixa de aceitar ligações (a resposta certa já chegou e não há outra volta). A ligação guardada não é afetada.
    public void StopListening()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        _listener.Stop();
    }

    private TcpClient? Take()
    {
        lock (_gate)
        {
            var client = _held;
            _held = null;
            return client;
        }
    }

    // Lê até ao fim dos cabeçalhos (ou o tampão encher) e devolve a primeira linha. Ler os cabeçalhos todos evita que,
    // ao fechar, sobrem bytes por ler — o Windows respondia com um RST e o navegador perdia a página.
    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end >= 0 ? text[..end] : null;
    }

    // As respostas 400 e 404: só o texto fixo de quem chama.
    private static string MessagePage(string message)
        => "<!doctype html><meta charset=\"utf-8\"><title>Éteris</title>"
           + "<body style=\"font:16px system-ui;margin:3em;background:#111;color:#eee\"><p>" + WebUtility.HtmlEncode(message) + "</p>";

    // O único sítio que escreve uma resposta: os cabeçalhos de segurança vão em todas (200, 302, 400 e 404). Um separador
    // fechado ou lento não faz falhar a entrada.
    private static async Task WriteAsync(NetworkStream stream, int status, string? location, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 302 => "Found", 400 => "Bad Request", _ => "Not Found" };
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
        if (location is not null) head.Append("Location: ").Append(location).Append("\r\n");
        head.Append("Content-Type: text/html; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Content-Security-Policy: ").Append(ContentSecurityPolicy).Append("\r\n")
            .Append("Connection: close\r\n\r\n");
        using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        write.CancelAfter(WriteTimeout);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), write.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, write.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
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

    // Fecha também a ligação guardada, se ninguém lhe respondeu: o separador nunca fica pendurado.
    public void Dispose()
    {
        Take()?.Dispose();
        _listener.Dispose();
    }
}

// As opções de um início de sessão. As tarefas seguintes acrescentam-lhe propriedades «init».
public sealed record SignInOptions(IExternalBrowser Browser, Action<string>? ShowUrl = null, string? LoginHint = null)
{
    // Corre com os tokens na mão e a página do navegador em espera: devolve o texto a mostrar. Sem ele (ou se falhar
    // ou passar de FinishTimeout) a página é a neutra.
    public Func<OAuthTokens, CancellationToken, Task<SignInPageKind>>? Finish { get; init; }

    // Uma vez por início de sessão bem-sucedido, antes de Finish (chamar a atenção para a janela do Éteris).
    public Action? SignedIn { get; init; }

    public TimeSpan FinishTimeout { get; init; } = TimeSpan.FromSeconds(10);

    // Prazo de toda a entrada (as duas voltas juntas). null = GoogleOAuth.SignInTimeout (5 min).
    public TimeSpan? Timeout { get; init; }
}

// OAuth 2.0 para apps de computador (PKCE + loopback), com o navegador do sistema: a Google bloqueia o início de
// sessão dentro de motores embutidos como a CEF («disallowed_useragent»), e o Éteris também não o quer lá dentro.
public sealed class GoogleOAuth(HttpClient http, OAuthClientConfig config, Func<long> nowMs)
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    public const string PromptSelectAccount = "select_account";
    public const string PromptConsent = "consent";

    // «select_account» só pede a escolha da conta: quem já deu o acesso não volta a passar pelo ecrã de consentimento.
    // «access_type=offline» pede o token de renovação (sincronizar sem pedir sessão a cada arranque). Se a Google não o
    // devolver, SignInAsync repete uma vez com «consent» (que o força). O «login_hint» só vem de quem chama, na reentrada.
    public string BuildAuthorizationUrl(string redirectUri, string challenge, string state,
        string prompt = PromptSelectAccount, string? loginHint = null)
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
            ["access_type"] = "offline",
            ["prompt"] = prompt,
        };
        if (CleanLoginHint(loginHint) is { } hint) query["login_hint"] = hint;
        return GoogleEndpoints.Authorize + "?" + string.Join('&', query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    // Aparado, sem caracteres de controlo, com «@» e no máximo 254 caracteres; senão null (omite-se em silêncio).
    public static string? CleanLoginHint(string? email)
    {
        var hint = email?.Trim();
        if (string.IsNullOrEmpty(hint) || hint.Length > 254 || !hint.Contains('@') || hint.Any(char.IsControl)) return null;
        return hint;
    }

    // Fluxo completo. «showUrl» é chamado quando não há navegador do sistema utilizável (mostrar para copiar).
    public Task<OAuthTokens> SignInAsync(IExternalBrowser browser, Action<string>? showUrl, CancellationToken cancellationToken)
        => SignInAsync(new SignInOptions(browser, showUrl), cancellationToken);

    // O separador do navegador fica à espera durante a troca do código e o Finish de quem chama, e só então recebe a
    // página com o texto verdadeiro (nunca «Sessão iniciada» se a troca falhou).
    public async Task<OAuthTokens> SignInAsync(SignInOptions options, CancellationToken cancellationToken)
    {
        // Um só recetor e um só prazo para as duas voltas.
        using var receiver = LoopbackReceiver.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout ?? SignInTimeout);
        var hint = CleanLoginHint(options.LoginHint);
        var tokens = await AuthorizeOnceAsync(receiver, options, PromptSelectAccount, hint, redirect: false, timeout.Token, cancellationToken).ConfigureAwait(false);
        if (tokens.RefreshToken is null)
        {
            // Sem token de renovação a sessão morria ao fim de uma hora: repete-se uma vez, com «consent» e «state»/PKCE
            // novos, no mesmo separador (302 para o ecrã da Google).
            tokens = await AuthorizeOnceAsync(receiver, options, PromptConsent, hint, redirect: true, timeout.Token, cancellationToken).ConfigureAwait(false);
            if (tokens.RefreshToken is null)
            {
                await receiver.CompleteAsync(SignInPageKind.Refused, CancellationToken.None).ConfigureAwait(false);
                throw new OAuthException("A Google não deu ao Éteris a autorização para manter a sessão. Tente entrar outra vez.");
            }
        }
        // Não há outra volta: a porta fecha-se já, enquanto a página espera.
        receiver.StopListening();
        options.SignedIn?.Invoke();
        var page = SignInPageKind.Neutral;
        if (options.Finish is { } finish)
        {
            // Demorou, falhou ou foi cancelado: a página neutra, e a entrada continua (os tokens são devolvidos).
            try { page = await finish(tokens, cancellationToken).WaitAsync(options.FinishTimeout, cancellationToken).ConfigureAwait(false); }
            catch (Exception) { page = SignInPageKind.Neutral; }
        }
        await receiver.CompleteAsync(page, CancellationToken.None).ConfigureAwait(false);
        return tokens;
    }

    // «redirect»: a 2.ª volta manda o separador que está à espera para o ecrã da Google em vez de abrir outro.
    private async Task<OAuthTokens> AuthorizeOnceAsync(LoopbackReceiver receiver, SignInOptions options, string prompt, string? loginHint,
        bool redirect, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        var (verifier, challenge) = Pkce.Create();
        var state = Kdf.Base64Url(RandomNumberGenerator.GetBytes(16));
        var url = BuildAuthorizationUrl(receiver.RedirectUri, challenge, state, prompt, loginHint);
        if (redirect) await receiver.RedirectAsync(url, cancellationToken).ConfigureAwait(false);
        else if (!options.Browser.TryOpen(url)) options.ShowUrl?.Invoke(url);

        AuthorizationResponse response;
        try { response = await receiver.WaitAsync(state, timeoutToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException("O início de sessão no navegador demorou demasiado. Tente de novo.", failure: OAuthFailure.TimedOut);
        }
        if (response.Error is { } error)
        {
            await receiver.CompleteAsync(SignInPageKind.Refused, CancellationToken.None).ConfigureAwait(false);
            throw new OAuthException(ErrorMessage(error), failure: OAuthFailure.Refused);
        }
        if (response.State != state || response.Code is not { Length: > 0 } code)
        {
            await receiver.CompleteAsync(SignInPageKind.Refused, CancellationToken.None).ConfigureAwait(false);
            throw new OAuthException("A Google não devolveu código de autorização.");
        }
        try { return await ExchangeCodeAsync(code, verifier, receiver.RedirectUri, cancellationToken).ConfigureAwait(false); }
        catch (OAuthException e) when (e.Failure == OAuthFailure.MissingScope)
        {
            await receiver.CompleteAsync(SignInPageKind.DriveDenied, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            // Troca recusada, sem rede ou cancelada: o separador sabe que não entrou.
            await receiver.CompleteAsync(SignInPageKind.Refused, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // O «error» da query vem de quem chamou o recetor: só um código curto em minúsculas entra na mensagem.
    internal static string ErrorMessage(string error)
    {
        if (error == "access_denied") return "O acesso foi recusado no ecrã da Google.";
        var safe = error.Length is > 0 and <= 40 && error.All(c => c is (>= 'a' and <= 'z') or '_');
        return safe ? "A Google devolveu um erro: " + error : "A Google devolveu um erro.";
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
            throw error == "invalid_grant"
                ? new OAuthException("A autorização da Google expirou ou foi retirada. Entre outra vez.", needsSignIn: true)
                : new OAuthException("A Google recusou o pedido de sessão (" + error + ").");
        }
        var scope = parsed.Scope ?? string.Empty;
        // No ecrã da Google o utilizador pode desmarcar a permissão: sem ela não há sincronização.
        if (!scope.Split(' ').Contains(GoogleEndpoints.Scope))
            throw new OAuthException("Sem a permissão para a pasta da aplicação no Google Drive, o Éteris não consegue sincronizar.", needsSignIn: true,
                OAuthFailure.MissingScope);
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
// «Sair» pode chegar a meio de uma renovação (o pedido da conta renova o token): cada sessão tem uma geração, que
// SignOutAsync e Use (um novo início de sessão) mudam, e uma renovação só fica com os tokens novos — e só os grava —
// se a geração ainda for a mesma de quando começou. Sem isto os tokens voltavam ao disco depois de «Sair».
public sealed class GoogleSession(GoogleOAuth oauth, TokenStore store, Func<long> nowMs)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private OAuthTokens? _tokens = store.Load();
    private long _generation;

    public bool IsSignedIn
    {
        get { lock (_state) return _tokens is not null; }
    }

    public void Use(OAuthTokens tokens)
    {
        lock (_state)
        {
            _generation++;
            _tokens = tokens;
            store.Save(tokens);
        }
    }

    public async Task<string> AccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OAuthTokens tokens;
            long generation;
            lock (_state)
            {
                tokens = _tokens ?? throw new OAuthException("Sem sessão iniciada na Google.");
                generation = _generation;
            }
            if (!forceRefresh && tokens.ExpiresAtMs > nowMs()) return tokens.AccessToken;
            if (tokens.RefreshToken is not { } refresh) throw new OAuthException("A sessão da Google expirou. Entre outra vez.", needsSignIn: true);
            var renewed = await oauth.RefreshAsync(refresh, cancellationToken).ConfigureAwait(false);
            lock (_state)
            {
                // Saiu-se (ou entrou-se com outra conta) durante a renovação: estes tokens já não são desta sessão.
                if (generation != _generation) throw new OAuthException("A sessão da Google terminou entretanto.");
                _tokens = renewed;
                store.Save(renewed);
            }
            return renewed.AccessToken;
        }
        finally { _gate.Release(); }
    }

    // A metade local de «Sair», sem rede: apaga os tokens deste aparelho já, e de modo que nenhuma renovação em curso os
    // volte a gravar (a geração sobe). A autorização na Google fica dada (os outros computadores continuam ligados).
    public void SignOut() => Forget();

    // Sair: SignOut e revoga na Google (melhor esforço).
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var tokens = Forget();
        if (tokens is not null) await oauth.RevokeAsync(tokens.RefreshToken ?? tokens.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    private OAuthTokens? Forget()
    {
        lock (_state)
        {
            _generation++;
            var tokens = _tokens;
            _tokens = null;
            store.Delete();
            return tokens;
        }
    }
}
