using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Eteris.Browser.Sync;
using Eteris.Sync.Google;

namespace Eteris.Browser.Tests.Sync;

// A conta no menu e nas Definições: nome e e-mail pedidos ao Drive (about.get, âmbito drive.appdata já concedido),
// guardados cifrados, apagados ao sair, e pedidos só depois de uma ação do utilizador — nunca no arranque.
public class GoogleAccountTests
{
    private long _now = 1_762_560_000_000;

    private long Now() => _now;

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "eteris-account-tests", Guid.NewGuid().ToString("N"), name);

    // Uma sessão com a Google já iniciada (tokens no disco) e o serviço da conta, como o Éteris os monta ao arrancar.
    private (GoogleSession Session, GoogleAccountService Accounts, GoogleAccountStore Store) Signed(FakeGoogle google, ISecretProtector? protector = null)
    {
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var tokens = new TokenStore(TempFile("google.tokens"), new Xor());
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
        Assert.DoesNotMatch(new Regex(@"setInterval|setTimeout\(\(\) => ask\('sync-account'"), page);
    }

    [Fact]
    public void SettingsText_SaysWhichAccount()
    {
        Assert.StartsWith("Ligado como Ana Teste (ana@example.com) ao Google Drive", GoogleSync.SignedInText(new GoogleAccount("Ana Teste", "ana@example.com")));
        Assert.StartsWith("Ligado ao Google Drive (pasta oculta", GoogleSync.SignedInText(null));
    }

    // ---- a linha do menu ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, true, "None")]     // sem a opção experimental: o menu fica como está
    [InlineData(false, false, false, "None")]
    [InlineData(true, false, false, "SignIn")]   // opção ligada, sem sessão
    [InlineData(true, false, true, "Account")]   // opção ligada, com sessão
    [InlineData(true, true, true, "None")]       // janela privada: nada da conta
    [InlineData(true, true, false, "None")]
    public void MenuRow_ThreeSituations_AndNothingInPrivateWindows(bool syncPreview, bool isPrivate, bool signedIn, string expected)
        => Assert.Equal(expected, MainWindow.BrowserMenuAccountRow(syncPreview, isPrivate, signedIn).ToString());

    [Fact]
    public void MenuRow_ShowsInitialNameEmailAndState_WithAnAccessibleName()
    {
        var (texts, name) = OnSta(() => Row(MainWindow.MenuAccountRow.Account, new GoogleAccount("ana Teste", "ana.teste@example.com")));
        Assert.Equal(new[] { "A", "ana Teste", "ana.teste@example.com", "Ligado ao Google Drive" }, texts);
        Assert.Equal("Conta Google: ana Teste (ana.teste@example.com). Ligado ao Google Drive. Abrir a sincronização nas Definições", name);

        // Sessão sem conta ainda (pedido em curso ou falhado): sem nome, mas diz que está ligada.
        var (pending, _) = OnSta(() => Row(MainWindow.MenuAccountRow.Account, null));
        Assert.Equal(new[] { "", "Conta Google ligada", "Ligado ao Google Drive" }, pending);

        var (signIn, signInName) = OnSta(() => Row(MainWindow.MenuAccountRow.SignIn, null));
        Assert.Equal("Entrar com o Google", signIn[1]);
        Assert.Equal("Entrar com o Google. Abre a sincronização nas Definições", signInName);
    }

    [Fact]
    public void Menu_AccountRowIsTheFirstKeyboardStop_AndOpensTheSyncSection()
    {
        var menu = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.BrowserMenu.cs"));
        Assert.Contains("buttons.Insert(0, account);", menu);
        Assert.Contains("OpenInternal(SettingsPage.Url + \"#sincronizacao\")", menu);
    }

    private static (string[] Texts, string Name) Row(MainWindow.MenuAccountRow kind, GoogleAccount? account)
    {
        var button = MainWindow.BrowserMenuAccountButton(kind, account, new Style(typeof(Button)));
        return (Texts((DependencyObject)button.Content).Select(t => t.Text).ToArray(), AutomationProperties.GetName(button));
    }

    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Texts(child)) yield return found;
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
        return result;
    }

    // «Cifra» de teste: muda todos os bytes (o texto não fica legível no ficheiro).
    private sealed class Xor : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }
}
