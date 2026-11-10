using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Model;

namespace Eteris.Browser.Tests.Sync;

// Os pontos de entrada do «Entrar com o Google» fora das Definições e do menu (0.2.15, T13): boas-vindas, a oferta
// depois de guardar uma palavra-passe, a paleta e as páginas do cofre. Decidir se aparecem só lê disco; o clique é um
// só (SignInEntry.Begin), sem pista de conta; nada na Nova aba, nada no arranque.
public class SignInEntryTests
{
    private static string Src => Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Src, relative)).Replace("\r\n", "\n");

    // Só sem sessão, nunca na janela privada, nunca sem o identificador da aplicação (nem a pasta de teste).
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void Offers_OnlyWithoutSession_NeverPrivate_NeverWithoutTheAppId(bool isPrivate, bool available, bool connected, bool expected)
        => Assert.Equal(expected, SignInEntry.Offers(isPrivate, available, connected));

    // O menu ⋯ e os outros pontos de entrada nunca discordam.
    [Fact]
    public void Offers_AgreesWithTheMenuRow()
    {
        foreach (var p in new[] { false, true })
            foreach (var a in new[] { false, true })
                foreach (var c in new[] { false, true })
                    Assert.Equal(MainWindow.BrowserMenuAccountRow(p, a, c) == MainWindow.MenuAccountRow.SignIn, SignInEntry.Offers(p, a, c));
    }

    // Só ao guardar uma palavra-passe nova pela bolha, sem sessão, e uma só vez por perfil.
    [Fact]
    public void OffersAfterSave_OnlyForANewPassword_Once()
    {
        Assert.True(SignInEntry.OffersAfterSave(SaveOfferKind.Save, offers: true, alreadyOffered: false));
        Assert.False(SignInEntry.OffersAfterSave(SaveOfferKind.Update, offers: true, alreadyOffered: false));
        Assert.False(SignInEntry.OffersAfterSave(SaveOfferKind.ConfirmGenerated, offers: true, alreadyOffered: false));
        Assert.False(SignInEntry.OffersAfterSave(SaveOfferKind.None, offers: true, alreadyOffered: false));
        Assert.False(SignInEntry.OffersAfterSave(SaveOfferKind.Save, offers: true, alreadyOffered: true));
        Assert.False(SignInEntry.OffersAfterSave(SaveOfferKind.Save, offers: false, alreadyOffered: false));
    }

    [Fact]
    public void Texts_AreThePlanTexts()
    {
        Assert.Equal("Entrar com o Google", SignInEntry.Label);
        Assert.Equal("Entrar com o Google (sincronização)", SignInEntry.PaletteTitle);
        Assert.Equal("Sincronize favoritos e palavras-passe, cifrados ponta a ponta", SignInEntry.PaletteSubtitle);
        Assert.Equal("Ter as suas palavras-passe noutros computadores?", SignInEntry.AfterSaveQuestion);
    }

    // Quem chama o clique: só as rotas das páginas internas (depois de um clique na página), a paleta e, no cofre, o
    // aviso depois de guardar e o anfitrião das páginas. Nada no arranque, nada na Nova aba.
    [Fact]
    public void Begin_IsOnlyCalledFromUserActions()
    {
        var callers = Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("SignInEntry.Begin", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Src, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Browser/EterisSchemeHandlerFactory.cs", "MainWindow.Palette.cs", "MainWindow.Vault.cs" }, callers);

        foreach (var file in new[] { "App.xaml.cs", "MainWindow.xaml.cs", "MainWindow.Session.cs", "Browser/WelcomePage.cs", "Browser/NewTabPage.cs", "Browser/NewTabPrecompute.cs" })
        {
            var text = Read(file);
            Assert.DoesNotContain("SignInEntry", text, StringComparison.Ordinal);
            Assert.DoesNotContain("BeginSignIn", text, StringComparison.Ordinal);
        }
    }

    // A marca «oferta já mostrada» é deste computador: não viaja na sincronização nem no perfil exportado.
    [Fact]
    public void SignInOffered_StaysOnThisComputer()
    {
        Assert.DoesNotContain(Collections.SyncableSettings, k => k.Contains("signIn", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ProfileTransfer.SettingValues(new UserSettings { SignInOffered = true }).Keys,
            k => k.Contains("signIn", StringComparison.OrdinalIgnoreCase));

        var root = GoogleSyncTests.FindRoot();
        Assert.DoesNotContain("SignInOffered", File.ReadAllText(Path.Combine(root, "src", "Eteris.Sync", "Model", "SyncRecord.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("SignInOffered", Read("Sync/ProfileTransfer.cs"), StringComparison.Ordinal);
    }

    // As Definições (botões «Entrar com o Google» e «Entrar outra vez») e as boas-vindas usam o mesmo clique, sem pista
    // de conta; o menu ⋯ fica com os seus literais (T3).
    [Fact]
    public void SettingsRoute_AndTheEntryPoints_ShareOneBegin()
    {
        var factory = Read("Browser/EterisSchemeHandlerFactory.cs");
        Assert.Equal(2, Regex.Matches(factory, Regex.Escape("Sync.SignInEntry.Begin()")).Count);
        Assert.DoesNotContain("GoogleSync.BeginSignIn(", factory, StringComparison.Ordinal);

        var entry = Read("Sync/SignInEntry.cs");
        Assert.Contains("GoogleSync.BeginSignIn();", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("ReentryHint", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("loginHint", entry, StringComparison.Ordinal);

        Assert.DoesNotContain("SignInEntry", Read("MainWindow.BrowserMenu.cs"), StringComparison.Ordinal);
    }

    // Decisão D7: nada de conta na Nova aba.
    [Fact]
    public void NewTab_HasNoSignInEntry()
    {
        foreach (var file in new[] { "Browser/newtab.html", "Browser/NewTabPage.cs", "Browser/NewTabPrecompute.cs" })
        {
            var text = Read(file);
            foreach (var needle in new[] { "Entrar com o Google", "sync-google", "#sincronizacao", "SignInEntry", "BeginSignIn" })
                Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
        }
    }

    // Decidir se o botão aparece não faz rondas nem pedidos: só os dois valores lidos do disco, e o clique.
    [Fact]
    public void Deciding_OnlyReadsDisk()
    {
        var entry = Read("Sync/SignInEntry.cs");
        foreach (var needle in new[] { "Agent(", "Look(", "RunAsync", "StartAsync", "RequestAccount", "Task.Run", "HttpClient", "Timer" })
            Assert.DoesNotContain(needle, entry, StringComparison.Ordinal);

        var allowed = new[] { "SyncService.Available", "SyncService.Connected", "SyncService.FolderMode", "SyncService.Start()", "GoogleSync.BeginSignIn()" };
        var used = Regex.Matches(entry, @"\b(?:SyncService|GoogleSync)\.[A-Za-z]+(?:\(\))?").Select(m => m.Value).Distinct().ToArray();
        Assert.NotEmpty(used);
        Assert.All(used, u => Assert.Contains(u, allowed));
    }

    // A decisão vive só em SignInEntry: as boas-vindas, as páginas do cofre, a paleta e o aviso passam por ela.
    [Fact]
    public void EntryPoints_AppearOnlyThroughThePolicy()
    {
        var factory = Read("Browser/EterisSchemeHandlerFactory.cs");
        Assert.Matches(new Regex(@"WelcomePage\.Render\([^;]*Sync\.SignInEntry\.OffersNow\(privateContext\)"), factory);

        var vault = Read("MainWindow.Vault.cs");
        Assert.Contains("public bool OffersSignIn => Sync.SignInEntry.OffersNow(isPrivate: false);", vault, StringComparison.Ordinal);
        var save = Body(vault, "private void SaveOfferedPassword(");
        Assert.Contains("SignInEntry.OffersNow(IsPrivate)", save, StringComparison.Ordinal);

        var palette = Read("MainWindow.Palette.cs");
        Assert.Contains("SignInEntry.OffersNow(IsPrivate)", palette, StringComparison.Ordinal);

        foreach (var text in new[] { palette, Read("Browser/WelcomePage.cs") })
        {
            Assert.DoesNotContain("GoogleSync.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SyncService.", text, StringComparison.Ordinal);
        }
    }

    // O corpo de um método (da assinatura até à chaveta que fecha com a indentação de membro).
    internal static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return source[start..end];
    }
}
