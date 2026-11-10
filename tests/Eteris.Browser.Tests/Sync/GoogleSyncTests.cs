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

    // A Google cria o ficheiro e responde 503: o envio não se repete às cegas (ficavam dois com o mesmo nome) — lista
    // outra vez e substitui. E apagar leva também os duplicados que já lá estejam.
    [Fact]
    public async Task Drive_CreateThatFailsAfterCreating_LeavesOneFile_AndDeleteRemovesDuplicates()
    {
        var google = new FakeGoogle();
        var (drive, _) = Drive(google);
        google.CreateThenFail = true;
        await drive.WriteAsync("eteris/c/keys", [1]);
        Assert.Single(google.Files, f => f.Name == "eteris/c/keys");
        Assert.Equal(new byte[] { 1 }, await drive.ReadAsync("eteris/c/keys"));

        google.Put("eteris/c/keys", [2]);
        Assert.Single(await drive.ListAsync("eteris/"));
        await drive.DeleteAsync("eteris/c/keys");
        Assert.DoesNotContain(google.Files, f => f.Name == "eteris/c/keys");
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

    private static Dictionary<string, string> QueryOf(string url) => LoopbackReceiver.ParseQuery(new Uri(url).Query.TrimStart('?'));

    private GoogleOAuth NewOAuth(FakeGoogle google)
        => new(GoogleEndpoints.CreateClient(google), new OAuthClientConfig(google.ClientId, null), Now);

    [Fact]
    public void AuthorizationUrl_AsksToChooseTheAccount_NotConsent()
    {
        var oauth = new GoogleOAuth(new HttpClient(), new OAuthClientConfig("1-x.apps.googleusercontent.com", null), Now);
        var url = oauth.BuildAuthorizationUrl("http://127.0.0.1:51234/", "desafio", "estado");
        var query = QueryOf(url);
        Assert.Equal("select_account", query["prompt"]);
        Assert.DoesNotContain("consent", new Uri(url).Query);
        Assert.False(query.ContainsKey("login_hint"));
    }

    [Fact]
    public void AuthorizationUrl_LoginHint_OnlyWhenGiven()
    {
        var oauth = new GoogleOAuth(new HttpClient(), new OAuthClientConfig("1-x.apps.googleusercontent.com", null), Now);
        var url = oauth.BuildAuthorizationUrl("http://127.0.0.1:51234/", "desafio", "estado", loginHint: "ana.teste@example.com");
        Assert.Equal("ana.teste@example.com", QueryOf(url)["login_hint"]);
        Assert.True(GoogleEndpoints.IsAllowedBrowserUrl(new Uri(url)));
        foreach (var bad in new[] { null, "", "   ", "sem-arroba", "ana@example.com\nX=1", new string('a', 300) + "@example.com" })
        {
            var plain = oauth.BuildAuthorizationUrl("http://127.0.0.1:51234/", "desafio", "estado", loginHint: bad);
            Assert.False(QueryOf(plain).ContainsKey("login_hint"));
            Assert.True(GoogleEndpoints.IsAllowedBrowserUrl(new Uri(plain)));
        }
    }

    [Theory]
    [InlineData(GoogleOAuth.PromptConsent)]
    [InlineData(GoogleOAuth.PromptSelectAccount)]
    public void AuthorizationUrl_KeepsPkceStateAndScope_WithAnyPrompt(string prompt)
    {
        var oauth = new GoogleOAuth(new HttpClient(), new OAuthClientConfig("1-x.apps.googleusercontent.com", null), Now);
        var url = oauth.BuildAuthorizationUrl("http://127.0.0.1:51234/", "desafio", "estado", prompt);
        var query = QueryOf(url);
        Assert.Equal(prompt, query["prompt"]);
        Assert.Equal("https://www.googleapis.com/auth/drive.appdata", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("desafio", query["code_challenge"]);
        Assert.Equal("estado", query["state"]);
        Assert.Equal("http://127.0.0.1:51234/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.DoesNotContain("client_secret", url);
    }

    [Fact]
    public async Task SignIn_PassesTheHintToTheBrowser_OnlyOnReentry()
    {
        var google = new FakeGoogle();
        var oauth = NewOAuth(google);
        var withHint = new ConsentingBrowser(google);
        await oauth.SignInAsync(new SignInOptions(withHint, null, "ana.teste@example.com"), CancellationToken.None);
        await withHint.Done;
        Assert.Equal("ana.teste@example.com", QueryOf(withHint.Opened[0])["login_hint"]);

        var without = new ConsentingBrowser(google);
        await oauth.SignInAsync(without, null, CancellationToken.None);
        await without.Done;
        Assert.False(QueryOf(without.Opened[0]).ContainsKey("login_hint"));
    }

    [Fact]
    public async Task SignIn_WithoutARefreshToken_RepeatsOnceWithConsent()
    {
        var google = new FakeGoogle { OmitRefreshToken = 1 };
        var browser = new ConsentingBrowser(google);
        var tokens = await NewOAuth(google).SignInAsync(browser, null, CancellationToken.None);
        await browser.Done;
        Assert.Equal(2, browser.Opened.Count);
        var first = QueryOf(browser.Opened[0]);
        var second = QueryOf(browser.Opened[1]);
        Assert.Equal("select_account", first["prompt"]);
        Assert.Equal("consent", second["prompt"]);
        Assert.NotEqual(first["state"], second["state"]);
        Assert.NotEqual(first["code_challenge"], second["code_challenge"]);
        Assert.Equal(first["redirect_uri"], second["redirect_uri"]);
        Assert.Equal(google.RefreshToken, tokens.RefreshToken);
        Assert.Equal(2, google.Exchanges);
    }

    [Fact]
    public async Task SignIn_WithARefreshToken_DoesNotAskForConsent()
    {
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google);
        await NewOAuth(google).SignInAsync(browser, null, CancellationToken.None);
        await browser.Done;
        Assert.Single(browser.Opened);
        Assert.Equal(1, google.Exchanges);
    }

    [Fact]
    public async Task SignIn_StillWithoutARefreshToken_FailsAndDoesNotLoop()
    {
        var google = new FakeGoogle { OmitRefreshToken = 5 };
        var browser = new ConsentingBrowser(google);
        var e = await Assert.ThrowsAsync<OAuthException>(() => NewOAuth(google).SignInAsync(browser, null, CancellationToken.None));
        await browser.Done;
        Assert.False(e.NeedsSignIn);
        Assert.Equal("A Google não deu ao Éteris a autorização para manter a sessão. Tente entrar outra vez.", e.Message);
        Assert.Equal(2, browser.Opened.Count);
        Assert.Equal(2, google.Exchanges);
    }

    [Fact]
    public void SignInHint_NeverTravelsInAnEterisAddress()
    {
        var root = FindRoot();
        foreach (var file in new[] { Path.Combine("Browser", "EterisSchemeHandlerFactory.cs"), Path.Combine("Browser", "settings.html") })
        {
            var text = File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", file));
            Assert.DoesNotContain("login_hint", text);
            Assert.DoesNotContain("loginHint", text);
        }
        Assert.Contains("BeginSignIn(string? loginHint = null)", File.ReadAllText(Path.Combine(root, "src", "Eteris.Browser", "Sync", "GoogleSync.cs")));
    }

    // «Cancelar» que chega quando os tokens já estavam a ser usados: este computador fica sem sessão, e só localmente
    // (revogar na Google desligava os outros computadores da pessoa). A entrada a sério fica para a prova do dono.
    [Fact]
    public void Cancel_AfterTheCodeArrived_LeavesNoSession()
    {
        var sync = File.ReadAllText(Path.Combine(FindRoot(), "src", "Eteris.Browser", "Sync", "GoogleSync.cs")).Replace("\r\n", "\n");
        var begin = Regex.Match(sync, @"public static void BeginSignIn\(string\? loginHint = null\)\n    \{(?<b>.*?)\n    \}\n", RegexOptions.Singleline).Groups["b"].Value;
        Assert.Matches(new Regex(@"if \([^)\n]*cancel\.IsCancellationRequested[^)\n]*\) UndoSignIn\(\);"), begin);
        var undo = Regex.Match(sync, @"private static void UndoSignIn\(\)\n    \{(?<b>.*?)\n    \}\n", RegexOptions.Singleline).Groups["b"].Value;
        Assert.Contains("SyncService.SignOut(forgetKey: false);", undo);
        Assert.Contains("Accounts()?.Forget();", undo);
        Assert.Contains("Session()?.SignOut();", undo);
        Assert.DoesNotContain("SignOutAsync", undo);
        Assert.DoesNotContain("Revoke", undo);
        // O trabalho depois dos tokens: com os tokens guardados antes da conta, e a página escolhida pela sincronização.
        var after = Regex.Match(sync, @"private static async Task<SignInPageKind> AfterTokensAsync\(OAuthTokens tokens\)\n    \{(?<b>.*?)\n    \}\n", RegexOptions.Singleline).Groups["b"].Value;
        Assert.Contains("SyncService.StartAsync()", after);
        Assert.Contains("SignInFinish.WaitAsync(", after);
        Assert.Contains("Finish = ", begin);
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
        // Sem «Finish» o Éteris não sabe como ficou a sincronização: a página é a neutra.
        Assert.Contains("Sessão iniciada. Volte ao Éteris para continuar.", browser.Page);
        Assert.Equal(SignInPage.Html(SignInPageKind.Neutral), browser.Page);
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

    // ---- a página de fim (segura até se saber o que dizer) -------------------------------------------------------------

    private static SignInOptions Options(IExternalBrowser browser, Func<OAuthTokens, CancellationToken, Task<SignInPageKind>>? finish = null,
        Action? signedIn = null)
        => new(browser) { Finish = finish, SignedIn = signedIn };

    [Fact]
    public async Task SignIn_HoldsThePage_UntilFinishChoosesTheText()
    {
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decided = new TaskCompletionSource<SignInPageKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signIn = NewOAuth(google).SignInAsync(Options(browser, (_, _) =>
        {
            arrived.TrySetResult();
            return decided.Task;
        }), CancellationToken.None);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.False(browser.Done.IsCompleted, "a página saiu antes de o Éteris saber o que dizer");
        decided.SetResult(SignInPageKind.FirstDevice);
        await signIn;
        await browser.Done;
        Assert.Contains("O Éteris começou a sincronizar", browser.Page);
    }

    [Theory]
    [InlineData(SignInPageKind.FirstDevice)]
    [InlineData(SignInPageKind.NeedsApproval)]
    [InlineData(SignInPageKind.Resumed)]
    [InlineData(SignInPageKind.Neutral)]
    public async Task SignIn_Page_ShowsWhatFinishDecided(SignInPageKind kind)
    {
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google);
        await NewOAuth(google).SignInAsync(Options(browser, (_, _) => Task.FromResult(kind)), CancellationToken.None);
        await browser.Done;
        Assert.Equal(SignInPage.Html(kind), browser.Page);
    }

    [Fact]
    public async Task SignIn_FinishTooSlow_ShowsTheNeutralPage_AndStillReturnsTheTokens()
    {
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google);
        var never = new TaskCompletionSource<SignInPageKind>();
        var tokens = await NewOAuth(google).SignInAsync(Options(browser, (_, _) => never.Task) with { FinishTimeout = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);
        await browser.Done;
        Assert.Equal(google.ValidAccessToken, tokens.AccessToken);
        Assert.Equal(SignInPage.Html(SignInPageKind.Neutral), browser.Page);
    }

    [Fact]
    public async Task SignIn_FinishThrows_ShowsTheNeutralPage()
    {
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google);
        var tokens = await NewOAuth(google).SignInAsync(Options(browser, (_, _) => throw new IOException("disco cheio")), CancellationToken.None);
        await browser.Done;
        Assert.Equal(google.RefreshToken, tokens.RefreshToken);
        Assert.Equal(SignInPage.Html(SignInPageKind.Neutral), browser.Page);
    }

    [Fact]
    public async Task SignIn_Refused_ShowsTheErrorPage()
    {
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google) { Deny = "access_denied" };
        var finished = 0;
        var signedIn = 0;
        var e = await Assert.ThrowsAsync<OAuthException>(() => NewOAuth(google).SignInAsync(Options(browser, (_, _) =>
        {
            finished++;
            return Task.FromResult(SignInPageKind.FirstDevice);
        }, () => signedIn++), CancellationToken.None));
        await browser.Done;
        Assert.Equal(OAuthFailure.Refused, e.Failure);
        Assert.Equal("O acesso foi recusado no ecrã da Google.", e.Message);
        Assert.Equal(SignInPage.Html(SignInPageKind.Refused), browser.Page);
        Assert.Equal(0, finished);
        Assert.Equal(0, signedIn);
        Assert.Equal(0, google.Exchanges);
    }

    [Fact]
    public async Task SignIn_MissingDrivePermission_ShowsTheDrivePage()
    {
        var google = new FakeGoogle { GrantScope = false };
        var browser = new ConsentingBrowser(google);
        var signedIn = 0;
        var e = await Assert.ThrowsAsync<OAuthException>(() => NewOAuth(google).SignInAsync(Options(browser, (_, _) => Task.FromResult(SignInPageKind.FirstDevice),
            () => signedIn++), CancellationToken.None));
        await browser.Done;
        Assert.Equal(OAuthFailure.MissingScope, e.Failure);
        Assert.True(e.NeedsSignIn);
        Assert.Equal(SignInPage.Html(SignInPageKind.DriveDenied), browser.Page);
        Assert.Equal(0, signedIn);
    }

    [Fact]
    public async Task SignIn_ExchangeFails_NeverSaysSignedIn()
    {
        // O identificador da aplicação não confere: a Google recusa a troca do código (invalid_client).
        var google = new FakeGoogle();
        var oauth = new GoogleOAuth(GoogleEndpoints.CreateClient(google), new OAuthClientConfig("999-outra.apps.googleusercontent.com", null), Now);
        var browser = new ConsentingBrowser(google);
        var signedIn = 0;
        var e = await Assert.ThrowsAsync<OAuthException>(() => oauth.SignInAsync(Options(browser, (_, _) => Task.FromResult(SignInPageKind.FirstDevice),
            () => signedIn++), CancellationToken.None));
        await browser.Done;
        Assert.Equal(OAuthFailure.Other, e.Failure);
        Assert.Equal(SignInPage.Html(SignInPageKind.Refused), browser.Page);
        Assert.DoesNotContain("Sessão iniciada", browser.Page);
        Assert.Equal(0, signedIn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SignIn_Success_AsksForAttentionOnce(int omitRefreshToken)
    {
        var google = new FakeGoogle { OmitRefreshToken = omitRefreshToken };
        var browser = new ConsentingBrowser(google);
        var signedIn = 0;
        var signedInBeforeFinish = false;
        await NewOAuth(google).SignInAsync(Options(browser, (_, _) =>
        {
            signedInBeforeFinish = signedIn == 1;
            return Task.FromResult(SignInPageKind.FirstDevice);
        }, () => signedIn++), CancellationToken.None);
        await browser.Done;
        Assert.Equal(1, signedIn);
        Assert.True(signedInBeforeFinish);
        Assert.Equal(1 + omitRefreshToken, google.Exchanges);
    }

    [Fact]
    public async Task SignIn_WithoutARefreshToken_RedirectsTheSameTabToConsent()
    {
        var google = new FakeGoogle { OmitRefreshToken = 1 };
        var browser = new ConsentingBrowser(google);
        await NewOAuth(google).SignInAsync(Options(browser, (_, _) => Task.FromResult(SignInPageKind.Resumed)), CancellationToken.None);
        await browser.Done;
        Assert.Equal(1, browser.Opens);
        Assert.Equal(2, browser.Opened.Count);
        var answers = browser.Answers.Where(a => a.Status != HttpStatusCode.NotFound).ToList();
        Assert.Equal([HttpStatusCode.Redirect, HttpStatusCode.OK], answers.Select(a => a.Status));
        var location = new Uri(answers[0].Headers["Location"]);
        Assert.True(GoogleEndpoints.IsAllowedBrowserUrl(location));
        Assert.Equal("consent", QueryOf(location.OriginalString)["prompt"]);
        Assert.Equal(location.OriginalString, browser.Opened[1]);
        Assert.Equal(SignInPage.Html(SignInPageKind.Resumed), browser.Page);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(404)]
    public async Task Loopback_EveryAnswer_CarriesTheSafetyHeaders(int status)
    {
        var google = new FakeGoogle { OmitRefreshToken = 1 };
        var browser = new ConsentingBrowser(google) { ForgeFirst = true };
        await NewOAuth(google).SignInAsync(browser, null, CancellationToken.None);
        await browser.Done;
        var answer = browser.Answers.First(a => (int)a.Status == status);
        Assert.Equal("text/html; charset=utf-8", answer.Headers["Content-Type"]);
        Assert.Equal("no-store", answer.Headers["Cache-Control"]);
        Assert.Equal("no-referrer", answer.Headers["Referrer-Policy"]);
        Assert.Equal("nosniff", answer.Headers["X-Content-Type-Options"]);
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
            answer.Headers["Content-Security-Policy"]);
        Assert.Equal("close", answer.Headers["Connection"]);
    }

    private static bool PortAnswers(int port)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    [Fact]
    public async Task SignIn_TimesOut_WithATypedFailure()
    {
        var google = new FakeGoogle();
        string? shown = null;
        var started = DateTime.UtcNow;
        var e = await Assert.ThrowsAsync<OAuthException>(() => NewOAuth(google).SignInAsync(
            new SignInOptions(new NoBrowser(), url => shown = url) { Timeout = TimeSpan.FromMilliseconds(150) }, CancellationToken.None));
        Assert.Equal(OAuthFailure.TimedOut, e.Failure);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        var port = new Uri(QueryOf(shown!)["redirect_uri"]).Port;
        Assert.False(PortAnswers(port), "o recetor continua à escuta depois do prazo");
    }

    // 0.2.15 (T4): cada passo do início de sessão chega ao estado tipado que a barra da janela mostra.
    [Fact]
    public void GoogleSync_ReportsEveryStep_ToTheTracker()
    {
        var code = File.ReadAllText(Path.Combine(FindRoot(), "src", "Eteris.Browser", "Sync", "GoogleSync.cs"));
        foreach (var step in new[] { "Tracker.Begin(", "Tracker.ShowManualUrl(", "Tracker.SignedIn(", "Tracker.Finished(", "Tracker.Failed(" })
            Assert.Contains(step, code);
    }

    // «Sair» esquece o estado do início de sessão e o e-mail da reentrada (R12 da T4).
    [Fact]
    public void SignOut_ResetsTheTracker()
    {
        var code = File.ReadAllText(Path.Combine(FindRoot(), "src", "Eteris.Browser", "Sync", "GoogleSync.cs"));
        var start = code.IndexOf("public static async Task SignOutAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "SignOutAsync não encontrado");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.Contains("Tracker.Reset()", code[start..end]);
        Assert.Contains("internal static void ResetSignIn()", code);
    }

    // «Cancelar» com o recetor à escuta fecha a porta do loopback e a tentativa acaba em cancelamento (R16 da T4).
    [Fact]
    public async Task Cancel_WhileWaiting_ClosesTheLoopbackPort()
    {
        var google = new FakeGoogle();
        string? shown = null;
        using var cts = new CancellationTokenSource();
        var signIn = NewOAuth(google).SignInAsync(new SignInOptions(new NoBrowser(), url => shown = url), cts.Token);
        Assert.NotNull(shown);
        var port = new Uri(QueryOf(shown!)["redirect_uri"]).Port;
        Assert.True(PortAnswers(port), "o recetor devia estar à escuta antes do «Cancelar»");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () =>
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port);
        });
    }

    [Fact]
    public async Task Loopback_SilentConnection_DoesNotBlockTheRealAnswer()
    {
        using (var plain = LoopbackReceiver.Start()) Assert.Equal(TimeSpan.FromSeconds(5), plain.RequestLineTimeout);
        using var receiver = LoopbackReceiver.Start();
        receiver.RequestLineTimeout = TimeSpan.FromMilliseconds(100);
        // Uma ligação local que não envia nada: sem o prazo da linha, o recetor ficava preso nela até ao prazo geral.
        using var silent = new System.Net.Sockets.TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, receiver.Port);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var answer = receiver.WaitAsync("estado-certo", wait.Token);
        using var local = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var page = local.GetStringAsync(receiver.RedirectUri + "?state=estado-certo&code=abc");
        var response = await answer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("abc", response.Code);
        await receiver.CompleteAsync(SignInPageKind.Neutral, CancellationToken.None);
        Assert.Equal(SignInPage.Html(SignInPageKind.Neutral), await page);
    }

    [Fact]
    public async Task Loopback_WrongState_Gets400_AndTheWaitGoesOn()
    {
        var google = new FakeGoogle();
        string? shown = null;
        var signIn = NewOAuth(google).SignInAsync(new SignInOptions(new NoBrowser(), url => shown = url), CancellationToken.None);
        Assert.NotNull(shown);
        var redirect = QueryOf(shown!)["redirect_uri"];
        using (var local = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        {
            using var forged = await local.GetAsync(redirect + "?error=access_denied&state=errado");
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            var text = WebUtility.HtmlDecode(await forged.Content.ReadAsStringAsync());
            Assert.Contains("Pedido não reconhecido pelo Éteris.", text);
            Assert.DoesNotContain("access_denied", text);
        }
        Assert.False(signIn.IsCompleted, "um pedido com o «state» errado terminou a espera");
        var consent = new ConsentingBrowser(google);
        consent.TryOpen(shown!); // o pedido certo, a seguir
        var tokens = await signIn.WaitAsync(TimeSpan.FromSeconds(10));
        await consent.Done;
        Assert.Equal(google.ValidAccessToken, tokens.AccessToken);
        Assert.DoesNotContain("access_denied", consent.Page);
    }

    [Fact]
    public async Task Loopback_StopsListening_OnceTheRightAnswerArrived()
    {
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decided = new TaskCompletionSource<SignInPageKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signIn = NewOAuth(google).SignInAsync(Options(browser, (_, _) =>
        {
            arrived.TrySetResult();
            return decided.Task;
        }), CancellationToken.None);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(PortAnswers(new Uri(browser.Redirect).Port), "a porta continua aberta enquanto a página espera");
        decided.SetResult(SignInPageKind.FirstDevice);
        await signIn;
        await browser.Done;
        Assert.Equal(SignInPage.Html(SignInPageKind.FirstDevice), browser.Page);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", false)]
    [InlineData(null, false)] // 5 000 caracteres
    [InlineData("server_error", true)]
    public async Task SignIn_ErrorFromTheQuery_IsReducedToASafeCode(string? error, bool named)
    {
        error ??= new string('x', 5_000);
        var google = new FakeGoogle();
        var browser = new ConsentingBrowser(google) { Deny = error };
        var e = await Assert.ThrowsAsync<OAuthException>(() => NewOAuth(google).SignInAsync(browser, null, CancellationToken.None));
        await browser.Done;
        Assert.Equal(SignInPage.Html(SignInPageKind.Refused), browser.Page);
        Assert.Equal(OAuthFailure.Refused, e.Failure);
        Assert.DoesNotContain("<", e.Message);
        Assert.DoesNotContain("script", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(e.Message.Length <= 200);
        if (named) Assert.Contains(error, e.Message);
        else Assert.Equal("A Google devolveu um erro.", e.Message);
    }

    [Fact]
    public async Task Session_SignOut_ForgetsTheTokens_WithoutAnyRequest()
    {
        var google = new FakeGoogle();
        var dir = Path.Combine(Path.GetTempPath(), "eteris-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TokenStore(Path.Combine(dir, "google.tokens"), new PlainProtector());
            var session = new GoogleSession(NewOAuth(google), store, Now);
            session.Use(new OAuthTokens("a", google.RefreshToken, _now - 1, GoogleEndpoints.Scope)); // já expirado: a próxima leitura renova
            // Uma renovação que começou antes de sair e só acaba depois.
            var (arrived, release) = google.Hold("/token");
            var renewal = session.AccessTokenAsync(false, CancellationToken.None);
            await arrived.WaitAsync(TimeSpan.FromSeconds(10));
            var before = google.Requests.Count;
            session.SignOut();
            Assert.False(session.IsSignedIn);
            Assert.Null(store.Load());
            release();
            await Assert.ThrowsAsync<OAuthException>(() => renewal);
            Assert.False(session.IsSignedIn);
            Assert.Null(store.Load());
            Assert.Equal(before, google.Requests.Count);
            Assert.Empty(google.Revoked);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SignIn_StillWithoutARefreshToken_ShowsTheErrorPage()
    {
        var google = new FakeGoogle { OmitRefreshToken = 5 };
        var browser = new ConsentingBrowser(google);
        var finished = 0;
        var signedIn = 0;
        await Assert.ThrowsAsync<OAuthException>(() => NewOAuth(google).SignInAsync(Options(browser, (_, _) =>
        {
            finished++;
            return Task.FromResult(SignInPageKind.FirstDevice);
        }, () => signedIn++), CancellationToken.None));
        await browser.Done;
        Assert.Equal(SignInPage.Html(SignInPageKind.Refused), browser.Page);
        Assert.Equal(0, finished);
        Assert.Equal(0, signedIn);
        Assert.Equal(2, browser.Opened.Count);
        Assert.Equal(1, browser.Opens);
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
    [InlineData("https://www.googleapis.com/drive/v3/about", false)]                                    // about só com os dois campos
    [InlineData("https://www.googleapis.com/drive/v3/about?fields=*", false)]
    [InlineData("https://www.googleapis.com/drive/v3/about?fields=user(photoLink,permissionId),storageQuota", false)]
    [InlineData("https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)&x=1", false)]
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

    // Uma resposta do recetor, como o navegador a viu: o código e os cabeçalhos (nome → valores juntos por «, »).
    internal sealed record LoopbackAnswer(HttpStatusCode Status, Dictionary<string, string> Headers);

    // Faz o que o navegador do sistema e o utilizador fariam: abre o ecrã da Google, aceita e segue o redirecionamento.
    // Os redirecionamentos do recetor (302 para o consentimento) seguem-se à mão: o HttpClient seguia-os sozinho e ia
    // pedir o endereço da Google à rede a sério. Cada 302 conta em «Opened» (é outro ecrã da Google), mas não em «Opens»
    // (o navegador do sistema só foi aberto uma vez).
    private sealed class ConsentingBrowser(FakeGoogle google) : IExternalBrowser
    {
        public bool ForgeFirst { get; init; }
        // O ecrã da Google responde com este «error» (e o «state» certo) em vez de um código: o utilizador recusou.
        public string? Deny { get; init; }
        // Corre antes das respostas (com o endereço do recetor): para meter uma ligação muda à frente, por exemplo.
        public Func<string, Task>? Before { get; init; }
        public List<HttpStatusCode> Forged { get; } = [];
        public List<LoopbackAnswer> Answers { get; } = [];
        public string Page { get; private set; } = string.Empty;
        public string Redirect { get; private set; } = string.Empty;
        public Task Done { get; private set; } = Task.CompletedTask;
        public List<string> Opened { get; } = [];
        public int Opens { get; private set; }

        public bool TryOpen(string url)
        {
            Opens++;
            var (state, code) = Consent(url);
            Done = Task.Run(async () =>
            {
                using var local = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                if (Before is not null) await Before(Redirect);
                await SendAsync(local, Redirect + "favicon.ico"); // o navegador pede isto também
                if (ForgeFirst)
                    foreach (var forged in new[] { "?error=nao_fui_eu&state=outro", "?error=access_denied", "?state=outro&code=roubado", "?code=sem-estado" })
                        Forged.Add(await SendAsync(local, Redirect + forged));
                while (true)
                {
                    var answer = Deny is { } error
                        ? $"{Redirect}?error={Uri.EscapeDataString(error)}&state={Uri.EscapeDataString(state)}"
                        : $"{Redirect}?state={Uri.EscapeDataString(state)}&code={Uri.EscapeDataString(code)}&scope=x";
                    using var response = await local.GetAsync(answer);
                    Record(response);
                    if (response.StatusCode == HttpStatusCode.Redirect)
                    {
                        (state, code) = Consent(response.Headers.Location!.OriginalString);
                        continue;
                    }
                    Page = await response.Content.ReadAsStringAsync();
                    return;
                }
            });
            return true;
        }

        // O ecrã da Google: confere o endereço, regista-o e devolve o «state» e o código que a Google mandaria.
        private (string State, string Code) Consent(string url)
        {
            lock (Opened) Opened.Add(url);
            var uri = new Uri(url);
            Assert.True(GoogleEndpoints.IsAllowedBrowserUrl(uri));
            var query = LoopbackReceiver.ParseQuery(uri.Query.TrimStart('?'));
            Redirect = query["redirect_uri"];
            return (query["state"], google.Authorize(query["code_challenge"], Redirect));
        }

        private async Task<HttpStatusCode> SendAsync(HttpClient local, string url)
        {
            using var response = await local.GetAsync(url);
            Record(response);
            return response.StatusCode;
        }

        private void Record(HttpResponseMessage response)
        {
            var headers = response.Headers.Concat(response.Content.Headers)
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (Answers) Answers.Add(new LoopbackAnswer(response.StatusCode, headers));
        }
    }
}
