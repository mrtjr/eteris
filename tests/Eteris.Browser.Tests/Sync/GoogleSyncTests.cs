using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Google;
using Eteris.Sync.Model;
using Eteris.Sync.Pairing;

namespace Eteris.Browser.Tests.Sync;

public class GoogleSyncTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;
    private long _now = 1_762_560_000_000;

    private long Now() => _now;

    private (GoogleDriveTransport Drive, HttpClient Http) Drive(FakeGoogle google)
    {
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var tokens = new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope);
        var session = new GoogleSession(oauth, new TokenStore(Path.Combine(Path.GetTempPath(), "eteris-sync-tests", Guid.NewGuid().ToString("N") + ".tokens"), new PlainProtector()), Now);
        session.Use(tokens);
        return (new GoogleDriveTransport(http, session.AccessTokenAsync, NoWait), http);
    }

    [Fact]
    public async Task Drive_WritesListsReadsReplacesAndDeletes_InTheAppDataFolder()
    {
        var google = new FakeGoogle { PageSize = 2 };
        var (drive, _) = Drive(google);
        for (var i = 0; i < 5; i++) await drive.WriteAsync($"eteris/c/d/x/{i}", [(byte)i, 1, 2]);
        var list = await drive.ListAsync("eteris/c/d/");
        Assert.Equal(5, list.Count); // três páginas
        Assert.Equal(new byte[] { 3, 1, 2 }, await drive.ReadAsync("eteris/c/d/x/3"));
        await drive.WriteAsync("eteris/c/d/x/3", [9]); // substituição: PATCH, não um segundo ficheiro
        Assert.Equal(new byte[] { 9 }, await drive.ReadAsync("eteris/c/d/x/3"));
        Assert.Equal(5, google.Files.Count);
        await drive.DeleteAsync("eteris/c/d/x/3");
        Assert.Null(await drive.ReadAsync("eteris/c/d/x/3"));
        Assert.Equal(4, google.Files.Count);
        Assert.Contains(google.Requests, r => r == "POST https://www.googleapis.com/upload/drive/v3/files");
        Assert.Contains(google.Requests, r => r.StartsWith("PATCH https://www.googleapis.com/upload/drive/v3/files/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drive_LargeFiles_UseResumableUpload()
    {
        var google = new FakeGoogle();
        var (drive, _) = Drive(google);
        var big = RandomNumberGenerator.GetBytes(GoogleDriveTransport.MultipartLimit + 10);
        await drive.WriteAsync("eteris/c/s/grande", big);
        Assert.Equal(big, await drive.ReadAsync("eteris/c/s/grande"));
        Assert.Contains(google.Requests, r => r.StartsWith("PUT https://www.googleapis.com/upload/drive/v3/files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drive_ExpiredToken_IsRenewedOnce_AndBusyServersAreRetried()
    {
        var google = new FakeGoogle();
        var (drive, _) = Drive(google);
        await drive.WriteAsync("eteris/c/keys", [1]);
        google.ExpireAccessToken();
        Assert.Equal(new byte[] { 1 }, await drive.ReadAsync("eteris/c/keys"));
        Assert.Equal(1, google.Refreshes);

        google.FailNext(HttpStatusCode.TooManyRequests);
        google.FailNext(HttpStatusCode.ServiceUnavailable);
        Assert.Single(await drive.ListAsync("eteris/"));
        google.FailNext(HttpStatusCode.ServiceUnavailable, 10);
        await Assert.ThrowsAsync<HttpRequestException>(() => drive.ListAsync("eteris/"));
    }

    [Fact]
    public async Task ThreeDevices_SyncThroughTheFakeDrive_AndTheDriveSeesNoPlaintext()
    {
        var google = new FakeGoogle();
        using var secret = AccountSecret.Create();
        var (d1, _) = Drive(google);
        var (d2, _) = Drive(google);
        var (d3, _) = Drive(google);
        var a = await SyncEngine.CreateAsync(d1, secret, DeviceIdentity.Create("PC"), Now);
        var b = await SyncEngine.JoinAsync(d2, secret, DeviceIdentity.Create("Portátil"), Now);
        var c = await SyncEngine.JoinAsync(d3, secret, DeviceIdentity.Create("Telemóvel", "android"), Now);
        a.Put(Collections.Bookmarks, "https://consulta.example/resultado", new JsonObject { ["title"] = "Resultado da análise" });
        _now += 10;
        b.SetNotes("PIN do cadeado: 4321");
        _now += 10;
        c.Put(Collections.Settings, "theme", new JsonObject { ["value"] = "light" });
        await SyncLab.SettleAsync(a, b, c);
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
        Assert.Equal(a.Fingerprint(), c.Fingerprint());
        Assert.Equal("PIN do cadeado: 4321", c.Notes);

        var all = string.Join("\n", google.Files.Select(f => f.Name + "\n" + Encoding.Latin1.GetString(f.Data)));
        foreach (var leak in new[] { "consulta", "Resultado", "4321", "cadeado", "light", "Portátil", "Telemóvel", "android" })
            Assert.DoesNotContain(leak, all, StringComparison.Ordinal);
        Assert.All(google.Requests, r => Assert.Matches("^(GET|POST|PATCH|PUT|DELETE) https://(www\\.googleapis\\.com/(upload/)?drive/v3/files|oauth2\\.googleapis\\.com/token)", r));
    }

    [Fact]
    public async Task Pairing_WorksThroughTheFakeDrive()
    {
        var google = new FakeGoogle();
        using var secret = AccountSecret.Create();
        var (pc, _) = Drive(google);
        var (phone, _) = Drive(google);
        // Espera a sério (cede a vez), senão o ciclo de espera do anfitrião nunca deixa o convidado avançar.
        Func<TimeSpan, CancellationToken, Task> yieldWait = async (_, _) => await Task.Delay(1);
        using var host = await PairingHost.StartAsync(pc, Now, yieldWait);
        using var guest = await PairingGuest.JoinAsync(phone, host.QrText, "Telemóvel", Now, yieldWait);
        var hostCode = host.WaitForGuestAsync();
        Assert.Equal(await guest.WaitForCodeAsync(), await hostCode);
        await host.ApproveAsync(secret, "PC");
        guest.ConfirmCode();
        using var received = await guest.WaitForSecretAsync();
        Assert.Equal(secret.ChainId, received.ChainId);
        await host.CleanUpAsync();
        Assert.Empty(google.Files);
    }

    // ---- OAuth (PKCE + loopback) ---------------------------------------------------------------------------------------

    [Fact]
    public void Pkce_ChallengeIsTheSha256OfTheVerifier()
    {
        var (verifier, challenge) = Pkce.Create();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.Equal(Kdf.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), challenge);
        // Valor calculado à parte (hashlib do Python): BASE64URL(SHA256(ASCII(verificador))), sem «=».
        Assert.Equal("7mnb0VCNLGOQoxpvIBbI7Z30gi-W_cht4F8R4WN7EJI", Pkce.Challenge("dBjftJeZ4CVP-mJ92K9ZEB5EnQkXyrnVlvTYqkDbW_A"));
    }

    [Fact]
    public void AuthorizationUrl_AsksOnlyForTheAppDataFolder_WithPkceAndLoopback()
    {
        var oauth = new GoogleOAuth(new HttpClient(), new OAuthClientConfig("1-x.apps.googleusercontent.com", null), Now);
        var url = new Uri(oauth.BuildAuthorizationUrl("http://127.0.0.1:51234/", "desafio", "estado"));
        Assert.True(GoogleEndpoints.IsAllowedBrowserUrl(url));
        var query = LoopbackReceiver.ParseQuery(url.Query.TrimStart('?'));
        Assert.Equal("https://www.googleapis.com/auth/drive.appdata", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("desafio", query["code_challenge"]);
        Assert.Equal("http://127.0.0.1:51234/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.DoesNotContain("client_secret", url.Query);
    }

    [Fact]
    public async Task SignIn_EndToEnd_ThroughTheLoopback()
    {
        var google = new FakeGoogle();
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, "nao-e-segredo"), Now);
        var browser = new ConsentingBrowser(google);
        var tokens = await oauth.SignInAsync(browser, null, CancellationToken.None);
        await browser.Done;
        Assert.Equal(google.ValidAccessToken, tokens.AccessToken);
        Assert.Equal(google.RefreshToken, tokens.RefreshToken);
        Assert.Contains("Pode fechar esta aba", browser.Page);
        Assert.StartsWith("http://127.0.0.1:", browser.Redirect);
    }

    [Fact]
    public async Task SignIn_WithoutAUsableBrowser_ShowsTheUrlToCopy()
    {
        var google = new FakeGoogle();
        var oauth = new GoogleOAuth(GoogleEndpoints.CreateClient(google), new OAuthClientConfig(google.ClientId, null), Now);
        string? shown = null;
        var consent = new ConsentingBrowser(google);
        var task = oauth.SignInAsync(new NoBrowser(), url => shown = url, CancellationToken.None);
        Assert.NotNull(shown);
        consent.TryOpen(shown!); // o utilizador colou o endereço noutro navegador
        await task;
    }

    [Fact]
    public async Task SignIn_IgnoresForgedRedirects_AndRefusesAMissingPermission()
    {
        // Uma página local (ou um processo que varre as portas) manda «/?error=…» ou um código seu ao recetor antes da
        // Google: leva 400, o início de sessão não aborta, e o texto dela não chega à interface.
        var google = new FakeGoogle();
        var oauth = new GoogleOAuth(GoogleEndpoints.CreateClient(google), new OAuthClientConfig(google.ClientId, null), Now);
        var browser = new ConsentingBrowser(google) { ForgeFirst = true };
        var tokens = await oauth.SignInAsync(browser, null, CancellationToken.None);
        await browser.Done;
        Assert.Equal(google.ValidAccessToken, tokens.AccessToken);
        Assert.Equal(4, browser.Forged.Count);
        Assert.All(browser.Forged, status => Assert.Equal(HttpStatusCode.BadRequest, status));

        var stingy = new FakeGoogle { GrantScope = false };
        var oauth2 = new GoogleOAuth(GoogleEndpoints.CreateClient(stingy), new OAuthClientConfig(stingy.ClientId, null), Now);
        var e = await Assert.ThrowsAsync<OAuthException>(() => oauth2.SignInAsync(new ConsentingBrowser(stingy), null, CancellationToken.None));
        Assert.Contains("pasta da aplicação", e.Message);
    }

    [Fact]
    public async Task SignOut_RevokesAtGoogle_AndForgetsTheTokens()
    {
        var google = new FakeGoogle();
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var dir = Path.Combine(Path.GetTempPath(), "eteris-test-" + Guid.NewGuid().ToString("N"));
        var store = new TokenStore(Path.Combine(dir, "google.tokens"), new PlainProtector());
        var session = new GoogleSession(oauth, store, Now);
        session.Use(new OAuthTokens("a", google.RefreshToken, _now + 1000, GoogleEndpoints.Scope));
        Assert.NotNull(store.Load());
        await session.SignOutAsync(CancellationToken.None);
        Assert.Equal([google.RefreshToken], google.Revoked);
        Assert.Null(store.Load());
        Assert.False(session.IsSignedIn);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ClientConfig_ReadsTheFileGoogleHandsOut_AndRejectsJunk()
    {
        var downloaded = """{"installed":{"client_id":"999-abc.apps.googleusercontent.com","project_id":"eteris","auth_uri":"https://accounts.google.com/o/oauth2/auth","client_secret":"GOCSPX-x","redirect_uris":["http://localhost"]}}""";
        var config = OAuthClientConfig.Parse(downloaded)!;
        Assert.Equal("999-abc.apps.googleusercontent.com", config.ClientId);
        Assert.Equal("GOCSPX-x", config.ClientSecret);
        Assert.Equal("1-y.apps.googleusercontent.com", OAuthClientConfig.Parse("""{"client_id":"1-y.apps.googleusercontent.com"}""")!.ClientId);
        Assert.Null(OAuthClientConfig.Parse("""{"client_id":"evil.example.com"}"""));
        Assert.Null(OAuthClientConfig.Parse("não é json"));
        Assert.Null(OAuthClientConfig.Load([Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N") + ".json")]));
    }

    // ---- a exceção à regra «pedidos só pelo motor» --------------------------------------------------------------------

    [Theory]
    [InlineData("https://oauth2.googleapis.com/token", true)]
    [InlineData("https://oauth2.googleapis.com/revoke", true)]
    [InlineData("https://www.googleapis.com/drive/v3/files", true)]
    [InlineData("https://www.googleapis.com/drive/v3/files/abc?alt=media", true)]
    [InlineData("https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart", true)]
    [InlineData("https://www.googleapis.com/upload/drive/v3/files/abc?uploadType=media", true)]
    [InlineData("http://www.googleapis.com/drive/v3/files", false)]                 // só https
    [InlineData("https://www.googleapis.com:8443/drive/v3/files", false)]
    [InlineData("https://www.googleapis.com/drive/v2/files", false)]
    [InlineData("https://www.googleapis.com/gmail/v1/users/me/messages", false)]    // nada além do Drive
    [InlineData("https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)", true)] // nome e e-mail da conta
    [InlineData("https://www.googleapis.com/drive/v3/about/x", false)]
    [InlineData("https://www.googleapis.com/drive/v3/changes", false)]
    [InlineData("https://oauth2.googleapis.com/tokeninfo", false)]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth", false)]            // esse só no navegador do sistema
    [InlineData("https://www.google-analytics.com/collect", false)]
    [InlineData("https://googleapis.com.evil.example/drive/v3/files", false)]
    [InlineData("https://user@www.googleapis.com/drive/v3/files", false)]
    public void HttpClient_OnlyReachesGoogleOAuthAndDrive(string url, bool allowed)
        => Assert.Equal(allowed, GoogleEndpoints.IsAllowedRequest(new Uri(url)));

    [Fact]
    public async Task HttpClient_RefusesAnythingElse_BeforeItLeavesTheProcess()
    {
        var google = new FakeGoogle();
        using var http = GoogleEndpoints.CreateClient(google);
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://example.com/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://www.googleapis.com/gmail/v1/users/me/messages"));
        Assert.Empty(google.Requests);
        Assert.True(GoogleEndpoints.IsAllowedBrowserUrl(new Uri("https://accounts.google.com/o/oauth2/v2/auth?x=1")));
        Assert.False(GoogleEndpoints.IsAllowedBrowserUrl(new Uri("https://accounts.google.com.evil.example/o/oauth2/v2/auth")));
    }

    // Fonte da biblioteca: o cliente HTTP só existe na pasta Google, e os únicos endereços escritos no código são os da
    // lista. Se isto falhar, a exceção cresceu: atualize docs/sincronizacao.md («A exceção de rede») e este teste.
    [Fact]
    public void SyncLibrary_HttpOnlyInTheGoogleFolder_AndOnlyListedAddresses()
    {
        var root = FindRoot();
        var src = Path.Combine(root, "src", "Eteris.Sync");
        string[] allowed =
        [
            "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", "https://oauth2.googleapis.com/revoke",
            "https://www.googleapis.com/drive/v3/files", "https://www.googleapis.com/upload/drive/v3/files",
            "https://www.googleapis.com/auth/drive.appdata", "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)",
        ];
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(file);
            var inGoogle = Path.GetRelativePath(src, file).StartsWith("Google" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            foreach (var api in new[] { "HttpClient", "HttpMessageHandler", "WebRequest", "Socket", "TcpListener", "WebSocket", "Dns." })
                if (!inGoogle) Assert.False(code.Contains(api, StringComparison.Ordinal), $"{Path.GetFileName(file)} usa {api} fora de Google/");
            foreach (Match m in Regex.Matches(code, "\"(https?://[^\"]*)\""))
                Assert.True(allowed.Any(a => m.Groups[1].Value.StartsWith(a, StringComparison.Ordinal)) || m.Groups[1].Value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} tem um endereço fora da lista: {m.Groups[1].Value}");
        }
        // A documentação regista a exceção.
        var doc = File.ReadAllText(Path.Combine(root, "docs", "sincronizacao.md"));
        Assert.Contains("A exceção de rede", doc);
        foreach (var host in new[] { "oauth2.googleapis.com", "accounts.google.com", "www.googleapis.com/drive/v3", "www.googleapis.com/upload/drive/v3", "www.googleapis.com/drive/v3/about" })
            Assert.Contains(host, doc);
    }

    // A integração no navegador (src/Eteris.Browser/Sync) não cria clientes HTTP, sockets nem DNS próprios: todo o
    // HttpClient vem de GoogleEndpoints.CreateClient (com o GoogleOnlyHandler à frente).
    [Fact]
    public void BrowserSyncFolder_HttpOnlyThroughTheGoogleClient()
    {
        var folder = Path.Combine(FindRoot(), "src", "Eteris.Browser", "Sync");
        var usesHttp = false;
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            foreach (var api in new[] { "new HttpClient", "HttpClientHandler", "SocketsHttpHandler", "HttpMessageHandler", "WebRequest", "WebClient",
                         "TcpClient", "TcpListener", "UdpClient", "new Socket", "WebSocket", "Dns." })
                Assert.False(code.Contains(api, StringComparison.Ordinal), $"{name} usa {api}: o cliente HTTP da sincronização é só GoogleEndpoints.CreateClient");
            Assert.DoesNotMatch(@"HttpClient\s*\(", code);
            foreach (Match m in Regex.Matches(code, "\"(https?://[^\"]*)\""))
                Assert.Fail($"{name} tem um endereço escrito no código: {m.Groups[1].Value}");
            if (code.Contains("HttpClient", StringComparison.Ordinal))
            {
                usesHttp = true;
                Assert.Contains("GoogleEndpoints.CreateClient(", code);
            }
        }
        Assert.True(usesHttp, "a varredura não encontrou o GoogleSync: mudou de pasta?");
        Assert.Contains("DNS do Windows", File.ReadAllText(Path.Combine(FindRoot(), "docs", "sincronizacao.md")));
    }

    [Fact]
    public void SyncLibrary_IsPlainNet_WithoutPackagesOrWindows()
    {
        var csproj = File.ReadAllText(Path.Combine(FindRoot(), "src", "Eteris.Sync", "Eteris.Sync.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csproj);
        Assert.DoesNotContain("PackageReference", csproj);
        Assert.DoesNotContain("UseWPF", csproj);
        Assert.DoesNotContain("windows", csproj, StringComparison.OrdinalIgnoreCase);
    }

    internal static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    private sealed class NoBrowser : IExternalBrowser
    {
        public bool TryOpen(string url) => false;
    }

    // Faz o que o navegador do sistema e o utilizador fariam: abre o ecrã da Google, aceita e segue o redirecionamento.
    private sealed class ConsentingBrowser(FakeGoogle google) : IExternalBrowser
    {
        public bool ForgeFirst { get; init; }
        public List<HttpStatusCode> Forged { get; } = [];
        public string Page { get; private set; } = string.Empty;
        public string Redirect { get; private set; } = string.Empty;
        public Task Done { get; private set; } = Task.CompletedTask;

        public bool TryOpen(string url)
        {
            var uri = new Uri(url);
            Assert.True(GoogleEndpoints.IsAllowedBrowserUrl(uri));
            var query = LoopbackReceiver.ParseQuery(uri.Query.TrimStart('?'));
            Redirect = query["redirect_uri"];
            var code = google.Authorize(query["code_challenge"], Redirect);
            var state = query["state"];
            Done = Task.Run(async () =>
            {
                using var local = new HttpClient();
                await local.GetAsync(Redirect + "favicon.ico"); // o navegador pede isto também
                if (ForgeFirst)
                    foreach (var forged in new[] { "?error=nao_fui_eu&state=outro", "?error=access_denied", "?state=outro&code=roubado", "?code=sem-estado" })
                        Forged.Add((await local.GetAsync(Redirect + forged)).StatusCode);
                Page = await local.GetStringAsync($"{Redirect}?state={Uri.EscapeDataString(state)}&code={Uri.EscapeDataString(code)}&scope=x");
            });
            return true;
        }
    }
}
