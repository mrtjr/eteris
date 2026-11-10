using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Sync.Google;

namespace Eteris.Browser.Tests.Sync;

// A conta no menu e nas Definições: nome e e-mail pedidos ao Drive (about.get, âmbito drive.appdata já concedido),
// guardados cifrados, apagados ao sair, e pedidos só depois de uma ação do utilizador — nunca no arranque.
public class GoogleAccountTests
{
    private long _now = 1_762_560_000_000;

    private string _tokenPath = "";

    private long Now() => _now;

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "eteris-account-tests", Guid.NewGuid().ToString("N"), name);

    // Uma sessão com a Google já iniciada (tokens no disco) e o serviço da conta, como o Éteris os monta ao arrancar.
    private (GoogleSession Session, GoogleAccountService Accounts, GoogleAccountStore Store) Signed(FakeGoogle google, ISecretProtector? protector = null, ISecretProtector? tokenProtector = null)
    {
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        _tokenPath = TempFile("google.tokens");
        var tokens = new TokenStore(_tokenPath, tokenProtector ?? new Xor());
        tokens.Save(new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope));
        var session = new GoogleSession(oauth, tokens, Now);
        var store = new GoogleAccountStore(TempFile("google.account"), protector ?? new Xor());
        return (session, new GoogleAccountService(http, session.AccessTokenAsync, store), store);
    }

    // ---- gravação cifrada --------------------------------------------------------------------------------------------

    [Fact]
    public void Store_SavesEncryptedWithDpapi_AndReadsBack()
    {
        var path = TempFile("google.account");
        var store = new GoogleAccountStore(path, new Dpapi("google-account"));
        store.Save(new GoogleAccount("Ana Teste", "ana.teste@example.com"));
        var raw = File.ReadAllBytes(path);
        foreach (var leak in new[] { "Ana", "ana.teste", "example.com" })
        {
            Assert.DoesNotContain(leak, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
            Assert.DoesNotContain(leak, Encoding.Unicode.GetString(raw), StringComparison.Ordinal);
        }
        Assert.Equal(new GoogleAccount("Ana Teste", "ana.teste@example.com"), store.Load());
        // Outra «entropia» (os tokens usam «google-tokens») não abre o ficheiro: separa os usos.
        Assert.Null(new GoogleAccountStore(path, new Dpapi("google-tokens")).Load());
        // Lixo ou ficheiro em falta: sem conta, sem exceção.
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Null(store.Load());
        store.Delete();
        Assert.False(File.Exists(path));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Account_InitialAndLabel()
    {
        Assert.Equal("A", new GoogleAccount("ana Teste", "x@example.com").Initial);
        Assert.Equal("Z", new GoogleAccount(null, "zé@example.com").Initial);
        Assert.Equal("?", new GoogleAccount(" ", null).Initial);
        Assert.Equal("B", new GoogleAccount("- Beatriz", null).Initial);   // pontuação no início não serve de inicial
        Assert.Equal("C", new GoogleAccount("«»", "carla@example.com").Initial);
        Assert.Equal("7", new GoogleAccount("_7 Mares", null).Initial);
        Assert.Equal("Ana Teste (ana@example.com)", new GoogleAccount("Ana Teste", "ana@example.com").Label);
        Assert.Equal("ana@example.com", new GoogleAccount(null, "ana@example.com").Label);
        Assert.Equal("Ana", new GoogleAccount("Ana", null).Label);
        Assert.True(new GoogleAccount("", " ").IsEmpty);
    }

    // ---- quando se pede ----------------------------------------------------------------------------------------------

    // A promessa da 0.2.11 («No arranque, nada sai para a Google»): montar a sessão e o serviço, ler o estado e a
    // conta guardada (o que o arranque, o menu e as Definições fazem para se desenhar) não faz pedido nenhum.
    [Fact]
    public void Startup_WithSessionAndNoAccount_SendsNothing()
    {
        var google = new FakeGoogle();
        var (session, accounts, _) = Signed(google);
        Assert.True(session.IsSignedIn);
        Assert.Null(accounts.Current);
        Assert.False(accounts.Pending);
        Assert.False(accounts.Failed);
        Assert.Empty(google.Requests);
    }

    [Fact]
    public async Task UserAction_FetchesNameAndEmail_Once_AndSavesThem()
    {
        var google = new FakeGoogle();
        var (session, accounts, store) = Signed(google);
        GoogleAccount? notified = null;
        accounts.Changed += a => notified = a;

        Assert.Null(accounts.RequestIfMissing(signedIn: false)); // sem sessão: nada
        Assert.Empty(google.Requests);

        var fetch = accounts.RequestIfMissing(session.IsSignedIn);
        Assert.NotNull(fetch);
        await fetch!;
        Assert.Equal(new[] { "GET https://www.googleapis.com/drive/v3/about" }, google.Requests);
        var expected = new GoogleAccount("Ana Teste", "ana.teste@example.com");
        Assert.Equal(expected, accounts.Current);
        Assert.Equal(expected, notified);
        Assert.Equal(expected, store.Load());

        // Já guardada: abrir o menu outra vez não pede nada, nem depois de reabrir o Éteris.
        Assert.Null(accounts.RequestIfMissing(true));
        var reopened = new GoogleAccountService(GoogleEndpoints.CreateClient(google), session.AccessTokenAsync, store);
        Assert.Equal(expected, reopened.Current);
        Assert.Null(reopened.RequestIfMissing(true));
        Assert.Single(google.Requests);
    }

    [Fact]
    public async Task Failure_AtMostOneFailedAttemptPerSession()
    {
        var google = new FakeGoogle();
        google.FailNext(HttpStatusCode.InternalServerError);
        var (session, accounts, store) = Signed(google);
        await accounts.RequestIfMissing(session.IsSignedIn)!;
        Assert.True(accounts.Failed);
        Assert.Null(accounts.Current);
        Assert.False(File.Exists(store.Path));
        // Abrir o menu ou as Definições outra vez não volta a tentar nesta sessão.
        Assert.Null(accounts.RequestIfMissing(true));
        Assert.Null(accounts.RequestIfMissing(true));
        Assert.Single(google.Requests);
    }

    [Fact]
    public async Task ExpiredAccessToken_RenewsOnce_AndRetries()
    {
        var google = new FakeGoogle();
        var (session, accounts, _) = Signed(google);
        google.ExpireAccessToken();
        await accounts.RequestIfMissing(session.IsSignedIn)!;
        Assert.Equal(1, google.Refreshes);
        Assert.Equal("Ana Teste", accounts.Current?.Name);
        Assert.Equal(new[] { "GET https://www.googleapis.com/drive/v3/about", "POST https://oauth2.googleapis.com/token", "GET https://www.googleapis.com/drive/v3/about" }, google.Requests);
    }

    [Fact]
    public async Task AfterSignIn_ReplacesTheOldAccount_AndSignOutDeletesIt()
    {
        var google = new FakeGoogle();
        var (_, accounts, store) = Signed(google);
        store.Save(new GoogleAccount("Conta Antiga", "antiga@example.com"));
        Assert.Equal("Conta Antiga", accounts.Current?.Name);
        google.AccountName = "Outra Pessoa";
        google.AccountEmail = null; // a Google pode omitir o e-mail
        await accounts.AfterSignIn();
        Assert.Equal(new GoogleAccount("Outra Pessoa", null), accounts.Current);
        Assert.Equal(new GoogleAccount("Outra Pessoa", null), store.Load());

        GoogleAccount? notified = new("x", "y");
        accounts.Changed += a => notified = a;
        accounts.Forget();
        Assert.Null(notified);
        Assert.Null(accounts.Current);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public async Task NetworkText_IsCleanedBeforeTheInterface()
    {
        var google = new FakeGoogle { AccountName = "  Ana\u0007\nTeste  ", AccountEmail = new string('a', 500) + "@example.com" };
        var (session, accounts, _) = Signed(google);
        await accounts.RequestIfMissing(session.IsSignedIn)!;
        Assert.Equal("AnaTeste", accounts.Current?.Name);
        Assert.Equal(200, accounts.Current?.Email?.Length);
    }

    // Quem chama o pedido da conta: só o menu (ao abrir), a rota eteris://settings/sync-account (a secção Sincronização
    // à vista depois de um gesto na página) e o fim de um início de sessão bem-sucedido. Nada no arranque, nada num
    // temporizador.
    [Fact]
    public void AccountRequest_OnlyAfterAUserAction_NeverAtStartupOrOnATimer()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var callers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("GoogleSync.RequestAccount(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Browser/EterisSchemeHandlerFactory.cs", "MainWindow.BrowserMenu.cs" }, callers);

        var menu = File.ReadAllText(Path.Combine(src, "MainWindow.BrowserMenu.cs"));
        // No menu, o pedido vem de BuildBrowserMenuAccount, que só ToggleBrowserMenu (Alt+F, clique) chama.
        Assert.Matches(new Regex(@"private Button\? BuildBrowserMenuAccount\([^)]*\)\s*\{(?:(?!\r?\n    \}).)*GoogleSync\.RequestAccount\(\)", RegexOptions.Singleline), menu);
        Assert.DoesNotContain("Timer", menu, StringComparison.Ordinal);

        var sync = File.ReadAllText(Path.Combine(src, "Sync", "GoogleSync.cs"));
        Assert.Single(Regex.Matches(sync, @"AfterSignIn\(\)"));
        Assert.Contains("Session()?.Use(tokens);", sync[..sync.IndexOf("AfterSignIn()", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.DoesNotContain("Timer", sync, StringComparison.Ordinal);

        // O arranque não toca na sincronização pelo Google.
        foreach (var startup in new[] { "App.xaml.cs", "MainWindow.xaml.cs" })
            Assert.DoesNotContain("GoogleSync", File.ReadAllText(Path.Combine(src, startup)), StringComparison.Ordinal);

        // Na página, «sync-account» só sai de askAccount, que exige um gesto (touched) e a secção à vista (seen).
        var page = File.ReadAllText(Path.Combine(src, "Browser", "settings.html"));
        Assert.Single(Regex.Matches(page, "'sync-account'"));
        Assert.Matches(new Regex(@"const askAccount = \(\) => \{\s*if \(!touched \|\| !seen \|\| accountKnown \|\| accountAsked"), page);
        Assert.DoesNotMatch(new Regex(@"(setInterval|setTimeout)\(\(\) => ask\('sync-account'"), page);
        // O único temporizador repetido da página é a conferência do kit de recuperação à vista (T5: pedido interno,
        // sem rede, só enquanto as palavras estão no ecrã).
        foreach (Match m in Regex.Matches(page, @"setInterval\("))
            Assert.StartsWith("setInterval(() => post('sync-kit-open'", page[m.Index..], StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsText_SaysWhichAccount()
    {
        Assert.StartsWith("Ligado como Ana Teste (ana@example.com) ao Google Drive", GoogleSync.SignedInText(new GoogleAccount("Ana Teste", "ana@example.com")));
        Assert.StartsWith("Ligado ao Google Drive (pasta oculta", GoogleSync.SignedInText(null));
    }

    // ---- a linha do menu ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, true, false, "SignIn")]   // sem sessão (sem a opção experimental, que saiu)
    [InlineData(false, true, true, "Account")]   // com sessão
    [InlineData(true, true, true, "None")]       // janela privada: nada da conta
    [InlineData(true, true, false, "None")]
    [InlineData(false, false, false, "None")]    // sem google-oauth.json: «Entrar com o Google» não se pode cumprir
    [InlineData(false, false, true, "None")]
    public void MenuRow_ThreeSituations_AndNothingInPrivateWindows(bool isPrivate, bool available, bool connected, string expected)
        => Assert.Equal(expected, MainWindow.BrowserMenuAccountRow(isPrivate, available, connected).ToString());

    // A linha da conta não empurra «Atalhos de teclado» e «Fechar janela» para fora do menu: sem teto fixo, o menu
    // cresce até à altura da janela (o menu inteiro com a linha da conta tem ~740 px; a janela das capturas, 860).
    // Ao abrir com o rato nenhuma linha fica realçada (nem o convite da conta, nem «Nova aba»), como no Chrome, no
    // Firefox e no Brave; pelo teclado, a primeira (a da conta, primeira no ecrã).
    [Fact]
    public void Menu_FitsWithTheAccountRow_AndTheMouseDoesNotHighlightTheInvitation()
    {
        Assert.Equal(860 - 74, MainWindow.BrowserMenuMaxHeight(860));
        Assert.Equal(1040 - 74, MainWindow.BrowserMenuMaxHeight(1040));
        Assert.Equal(254, MainWindow.BrowserMenuMaxHeight(200));

        Assert.Equal(0, MainWindow.BrowserMenuFirstFocus(byKeyboard: true));    // Alt+F: a conta, primeira no ecrã
        Assert.Equal(-1, MainWindow.BrowserMenuFirstFocus(byKeyboard: false));  // rato: nenhuma linha (a raiz do menu)

        var menu = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.BrowserMenu.cs"));
        // A barra de deslocamento, se a janela for baixa, tem as cores do tema; a letra do avatar usa a tinta do destaque.
        Assert.Contains("body.Resources.Add(typeof(ScrollBar), theme[\"BrowserMenuScrollBar\"]);", menu);
        // A linha da conta é um item de menu como as outras (leitor de ecrã, «1 de N»).
        Assert.Contains("var button = new BrowserMenuItemButton { Style = rowStyle, Padding = new Thickness(8, 6, 8, 6) };", menu);
        Assert.Contains("mark.SetResourceReference(TextBlock.ForegroundProperty, \"AccentInkBrush\");", menu);
        Assert.DoesNotContain("Color.FromRgb(0x0B, 0x0D, 0x13)", menu);
    }

    [Fact]
    public void MenuRow_ShowsInitialNameEmailAndState_WithAnAccessibleName()
    {
        // O estado é o da sincronização (aqui, sem ela ligada neste perfil de testes).
        var (texts, name) = OnSta(() => Row(MainWindow.MenuAccountRow.Account, new GoogleAccount("ana Teste", "ana.teste@example.com")));
        Assert.Equal(new[] { "A", "ana Teste", "ana.teste@example.com", "Sincronização desligada" }, texts);
        Assert.Equal("Conta Google: ana Teste (ana.teste@example.com). Sincronização desligada. Abre a sincronização nas Definições", name);

        // Sessão sem conta ainda (pedido em curso ou falhado): sem nome, mas diz que está ligada.
        var (pending, _) = OnSta(() => Row(MainWindow.MenuAccountRow.Account, null));
        Assert.Equal(new[] { "", "Conta Google ligada", "Sincronização desligada" }, pending);

        var (signIn, signInName) = OnSta(() => Row(MainWindow.MenuAccountRow.SignIn, null));
        Assert.Equal(new[] { "", "Entrar com o Google…", "Sincronize favoritos e palavras-passe, cifrados ponta a ponta" }, signIn);
        Assert.Equal("Entrar com o Google. Abre o seu navegador para escolher a conta Google", signInName);
        // A mesma frase como dica; a linha da conta (sem pausa) não a tem.
        Assert.Equal("Abre o seu navegador para escolher a conta Google", OnSta(() => RowTip(MainWindow.MenuAccountRow.SignIn, null)));
        Assert.Null(OnSta(() => RowTip(MainWindow.MenuAccountRow.Account, new GoogleAccount("ana Teste", "ana.teste@example.com"))));
    }

    // A linha entra direto: sem sessão começa o início de sessão; em pausa (a Google pediu para entrar outra vez)
    // entra com o e-mail guardado; só a linha da conta em ordem abre as Definições. No modo da pasta de teste o
    // «entrar» é ligar a pasta (como a rota das Definições).
    [Theory]
    [InlineData("SignIn", false, false, "SignIn")]
    [InlineData("SignIn", false, true, "SignIn")]
    [InlineData("Account", false, true, "SignInAgain")]
    [InlineData("Account", false, false, "OpenSettings")]
    [InlineData("SignIn", true, false, "StartTestFolder")]
    [InlineData("SignIn", true, true, "StartTestFolder")]
    [InlineData("Account", true, true, "OpenSettings")]
    [InlineData("Account", true, false, "OpenSettings")]
    [InlineData("None", false, false, "OpenSettings")]
    [InlineData("None", false, true, "OpenSettings")]
    [InlineData("None", true, false, "OpenSettings")]
    [InlineData("None", true, true, "OpenSettings")]
    public void MenuRow_Action_SignsInDirectly_AndOnlyTheAccountRowOpensSettings(string kind, bool folderMode, bool needsSignIn, string expected)
        => Assert.Equal(expected, MainWindow.BrowserMenuAccountAction(Enum.Parse<MainWindow.MenuAccountRow>(kind), folderMode, needsSignIn).ToString());

    [Fact]
    public void Menu_SignInRow_CallsBeginSignIn_NotTheSettingsPage()
    {
        var menu = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.BrowserMenu.cs"));
        Assert.Contains("GoogleSync.BeginSignIn();", menu);
        Assert.Contains("GoogleSync.BeginSignIn(GoogleSync.ReentryHint());", menu);
        Assert.Contains("RunBrowserMenuAction(popup, () => RunBrowserMenuAccount(kind))", menu);
        Assert.DoesNotContain("RunBrowserMenuAction(popup, () => OpenInternal(SettingsPage.Url + \"#sincronizacao\"))", menu);
        // A pausa e o e-mail só se leem no clique, nunca ao construir a linha.
        Assert.DoesNotContain("ReentryHint", menu[..menu.IndexOf("private Button? BuildBrowserMenuAccount", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Menu_AccountRowIsTheFirstKeyboardStop_AndOpensTheSyncSection()
    {
        var menu = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.BrowserMenu.cs"));
        Assert.Contains("buttons.Insert(0, account);", menu);
        Assert.Contains("OpenInternal(SettingsPage.Url + \"#sincronizacao\")", menu);
    }

    // ---- «Sair» a meio de um pedido ----------------------------------------------------------------------------------

    // Sessão antiga sem conta e token de acesso expirado: o pedido da conta renova o token e, a meio da renovação,
    // carrega-se em «Sair». A renovação que chega depois não volta a pôr a sessão nem grava google.tokens.
    [Fact]
    public async Task SignOut_DuringTokenRefresh_DoesNotBringTheTokensBack()
    {
        var google = new FakeGoogle();
        var (session, _, _) = Signed(google);
        _now += 7_200_000; // mais de 1 h: o token de acesso expirou
        var (arrived, release) = google.Hold("/token");
        var refresh = session.AccessTokenAsync(false, CancellationToken.None);
        await arrived.WaitAsync(TimeSpan.FromSeconds(10));

        // A revogação fica pendurada: a Google ainda responde à renovação pedida antes (o caso mais difícil).
        var (_, releaseRevoke) = google.Hold("/revoke");
        var signOut = session.SignOutAsync(CancellationToken.None);
        Assert.False(session.IsSignedIn); // os tokens deste aparelho saem logo, antes da resposta da Google
        Assert.False(File.Exists(_tokenPath));

        release();
        var error = await Assert.ThrowsAsync<OAuthException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("terminou", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, google.Refreshes); // a Google renovou, mas a resposta já não é desta sessão
        Assert.False(session.IsSignedIn);
        Assert.False(File.Exists(_tokenPath));
        releaseRevoke();
        await signOut.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(_tokenPath));
    }

    // O pedido da conta está na rede quando se carrega em «Sair»: a resposta que chega depois não grava o nome e o
    // e-mail (o ficheiro fica apagado), não muda a conta e não conta como tentativa falhada da sessão seguinte.
    [Fact]
    public async Task SignOut_DuringTheAccountRequest_LeavesNothingOnDisk()
    {
        var google = new FakeGoogle();
        var (session, accounts, store) = Signed(google);
        var (arrived, release) = google.Hold("/drive/v3/about");
        var fetch = accounts.RequestIfMissing(session.IsSignedIn)!;
        await arrived.WaitAsync(TimeSpan.FromSeconds(10));

        accounts.Forget();
        await session.SignOutAsync(CancellationToken.None);
        var notified = false;
        accounts.Changed += _ => notified = true;
        release();
        await fetch.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(File.Exists(store.Path));
        Assert.Null(accounts.Current);
        Assert.False(accounts.Failed);
        Assert.False(notified);
        Assert.False(File.Exists(_tokenPath));
    }

    // Sair e entrar com outra conta enquanto o pedido antigo está pendurado: fica a conta nova.
    [Fact]
    public async Task OldRequest_DoesNotOverwriteTheAccountOfTheNextSession()
    {
        var google = new FakeGoogle();
        var (session, accounts, store) = Signed(google);
        var (arrived, release) = google.Hold("/drive/v3/about");
        var old = accounts.RequestIfMissing(session.IsSignedIn)!;
        await arrived.WaitAsync(TimeSpan.FromSeconds(10));

        accounts.Forget();
        google.AccountName = "Outra Pessoa";
        google.AccountEmail = "outra@example.com";
        await accounts.AfterSignIn().WaitAsync(TimeSpan.FromSeconds(10));
        release();
        await old.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new GoogleAccount("Outra Pessoa", "outra@example.com"), accounts.Current);
        Assert.Equal(new GoogleAccount("Outra Pessoa", "outra@example.com"), store.Load());
        Assert.False(accounts.Failed);
    }

    // A renovação do token falha ao gravar (DPAPI ou ficheiro só de leitura/bloqueado): conta como a tentativa
    // falhada da sessão, e abrir o menu outra vez não volta à rede.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TokenSaveFailure_CountsAsTheFailedAttempt(bool cryptographic)
    {
        var google = new FakeGoogle();
        var failing = new FailingProtector();
        var (session, accounts, _) = Signed(google, tokenProtector: failing);
        _now += 7_200_000;
        failing.Fail = cryptographic ? new CryptographicException("DPAPI") : new UnauthorizedAccessException("só de leitura");
        await accounts.RequestIfMissing(session.IsSignedIn)!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(accounts.Failed);
        Assert.Null(accounts.RequestIfMissing(true));
        Assert.Equal(1, google.Refreshes);
        Assert.Equal(new[] { "POST https://oauth2.googleapis.com/token" }, google.Requests);
    }

    // O menu pede a conta no fio da interface: o pedido (token, proxy do Windows, rede) corre noutro fio, mesmo com o
    // fio da interface ocupado.
    [Fact]
    public void Request_RunsOffTheInterfaceThread()
    {
        var google = new FakeGoogle();
        var (session, _, store) = Signed(google);
        bool? onPool = null;
        var accounts = new GoogleAccountService(GoogleEndpoints.CreateClient(google), (force, ct) =>
        {
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            return session.AccessTokenAsync(force, ct);
        }, store);
        var done = OnSta(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var fetch = accounts.RequestIfMissing(true)!;
            return fetch.Wait(TimeSpan.FromSeconds(10)); // o Dispatcher não corre aqui: só termina se não depender dele
        });
        Assert.True(done);
        Assert.True(onPool);
        Assert.Equal("Ana Teste", accounts.Current?.Name);
    }

    // ---- Definições ---------------------------------------------------------------------------------------------------

    // Definições abertas numa janela privada: nem nome nem e-mail; a secção Sincronização fica indisponível.
    [Fact]
    public void PrivateWindow_SettingsShowNoAccount()
    {
        var account = new GoogleAccount("Ana Teste", "ana.teste@example.com");
        var status = new GoogleSync.Status("signedIn", GoogleSync.SignedInText(account), null, account);
        var normal = JsonSerializer.Serialize(SettingsPage.SyncGoogleData(status, isPrivate: false));
        Assert.Contains("Ana Teste", normal);
        Assert.Contains("\"account\":true", normal);
        var hidden = JsonSerializer.Serialize(SettingsPage.SyncGoogleData(status, isPrivate: true));
        foreach (var leak in new[] { "Ana", "ana.teste", "example.com" }) Assert.DoesNotContain(leak, hidden, StringComparison.Ordinal);
        Assert.Contains("\"account\":false", hidden);
        Assert.Contains("Ligado ao Google Drive", hidden);

        Assert.Contains("\"syncPrivate\":true", SettingsPage.Render(new UserSettings(), isPrivate: true));
        Assert.Contains("\"syncPrivate\":false", SettingsPage.Render(new UserSettings()));
        var factory = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("return SettingsRoute(uri, request.Url, privateContext, browser.Identifier);", factory);
        Assert.Contains("isPrivate: privateContext", factory);
        var page = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "settings.html"));
        Assert.Matches(new Regex(@"if \(data\.syncPrivate\) \{[^}]*body\.hidden = true; googleButton\.disabled = true;[^}]*return;", RegexOptions.Singleline), page);
    }

    // Cada pedido da secção tem o seu iframe (um sync-status automático não leva a resposta da exportação); só a
    // resposta mais recente do Google desenha; o gesto de «Sair» não pede a conta; com sessão, a linha chama-se «Conta
    // Google» e o botão diz de que se sai.
    [Fact]
    public void SettingsPage_RequestsDoNotShareAReply_AndSignOutIsLabelled()
    {
        var page = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "settings.html"));
        Assert.DoesNotContain("onReply", page, StringComparison.Ordinal);
        Assert.Contains("const sinkFor = handler =>", page, StringComparison.Ordinal);
        Assert.Contains("form.target = sinkFor(importReply).name;", page, StringComparison.Ordinal);
        Assert.Contains("if (seq < googlePainted) return;", page, StringComparison.Ordinal);
        Assert.Contains("if (!isSignOut(e)) { askAccount(); askLook(); }", page, StringComparison.Ordinal);
        Assert.Contains("googleTitle.textContent = g.state === 'signedIn' ? (g.folder ? 'Pasta de teste' : 'Conta Google') : g.folder ? 'Pasta de teste' : 'Entrar com o Google';", page, StringComparison.Ordinal);
        Assert.Contains("googleButton.setAttribute('aria-label', g.folder ? 'Desligar da pasta de teste' : 'Sair da conta Google');", page, StringComparison.Ordinal);
        Assert.Contains("id=\"googleButton\" aria-describedby=\"googleNote\"", page, StringComparison.Ordinal);
    }

    private static (string[] Texts, string Name) Row(MainWindow.MenuAccountRow kind, GoogleAccount? account)
    {
        var button = MainWindow.BrowserMenuAccountButton(kind, account, new Style(typeof(Button)));
        return (Texts((DependencyObject)button.Content).Select(t => t.Text).ToArray(), AutomationProperties.GetName(button));
    }

    private static string? RowTip(MainWindow.MenuAccountRow kind, GoogleAccount? account)
        => MainWindow.BrowserMenuAccountButton(kind, account, new Style(typeof(Button))).ToolTip as string;

    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Texts(child)) yield return found;
    }

    // Pelo StaRunner (a única thread STA dos testes: de fundo, com prazo e com o passo na mensagem; StaRunnerTests).
    private static T OnSta<T>(Func<T> work, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => StaRunner.Run(work, nameof(GoogleAccountTests) + "." + test);

    // Grava como a Xor, mas a gravação pode falhar como o DPAPI (CryptographicException) ou um ficheiro bloqueado.
    private sealed class FailingProtector : ISecretProtector
    {
        public Exception? Fail { get; set; }
        public byte[] Protect(byte[] data) => Fail is { } e ? throw e : data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    // «Cifra» de teste: muda todos os bytes (o texto não fica legível no ficheiro).
    private sealed class Xor : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
