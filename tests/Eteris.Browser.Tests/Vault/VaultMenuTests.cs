using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Vault;
using Eteris.Sync.Model;

namespace Eteris.Browser.Tests.Vault;

// B1 (docs/palavras-passe.md): a linha «Palavras-passe e preenchimento automático ›» no menu do Éteris, com o submenu
// de cinco entradas (como o do Chrome), as Definições e a paleta.
public class VaultMenuTests
{
    // As cinco entradas do submenu, pela ordem do Chrome, cada uma com a página e a secção.
    [Fact]
    public void Entries_FiveLikeChrome_WithTheirPages()
    {
        Assert.Equal("Palavras-passe e preenchimento automático", VaultPages.MenuTitle);
        Assert.Equal(
            ["Gestor de palavras-passe", "Pagamentos", "Informações de contacto", "Documentos de identificação", "Viagens"],
            VaultPages.Entries.Select(e => e.Title));
        Assert.Equal(
            ["eteris://passwords/", "eteris://autofill/#pagamentos", "eteris://autofill/#contacto", "eteris://autofill/#documentos", "eteris://autofill/#viagens"],
            VaultPages.Entries.Select(e => e.Url));
        // Iniciais diferentes: a primeira letra salta para cada uma.
        Assert.Equal(5, VaultPages.Entries.Select(e => char.ToLowerInvariant(e.Title[0])).Distinct().Count());
        Assert.All(VaultPages.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Glyph)));
    }

    // Na janela privada a linha aparece, mas as páginas recusam-se lá: as entradas abrem numa janela normal.
    [Fact]
    public void PrivateWindow_EntriesOpenInANormalWindow()
    {
        Assert.True(VaultPages.OpensInNormalWindow(isPrivate: true));
        Assert.False(VaultPages.OpensInNormalWindow(isPrivate: false));
        Assert.True(VaultPages.IsVaultPage("eteris://passwords/"));
        Assert.True(VaultPages.IsVaultPage("eteris://autofill/#viagens"));
        Assert.False(VaultPages.IsVaultPage("eteris://settings/"));
    }

    // Teclado como no Chrome: → (ou Enter, que ativa a linha) abre o submenu; ← ou Esc voltam ao menu; Alt+F fecha
    // o menu inteiro (não é «voltar»); ↑ ↓ e as letras andam dentro do submenu como no menu.
    [Theory]
    [InlineData(BrowserMenuKey.NextInGroup, Key.Right, false, true, BrowserSubmenuStep.Open)]
    [InlineData(BrowserMenuKey.NextInGroup, Key.Right, false, false, BrowserSubmenuStep.None)]
    [InlineData(BrowserMenuKey.PreviousInGroup, Key.Left, false, true, BrowserSubmenuStep.None)]
    [InlineData(BrowserMenuKey.PreviousInGroup, Key.Left, true, false, BrowserSubmenuStep.Back)]
    [InlineData(BrowserMenuKey.Close, Key.Escape, true, false, BrowserSubmenuStep.Back)]
    [InlineData(BrowserMenuKey.Close, Key.Escape, false, true, BrowserSubmenuStep.None)]
    [InlineData(BrowserMenuKey.Close, Key.F, true, false, BrowserSubmenuStep.None)]
    [InlineData(BrowserMenuKey.Next, Key.Down, true, false, BrowserSubmenuStep.None)]
    [InlineData(BrowserMenuKey.Initial, Key.P, true, false, BrowserSubmenuStep.None)]
    [InlineData(BrowserMenuKey.NextInGroup, Key.Right, true, false, BrowserSubmenuStep.None)]
    // Enter e Espaço na linha abrem o submenu pelo mesmo passo (com a primeira entrada realçada); dentro dele ativam.
    [InlineData(BrowserMenuKey.Activate, Key.Enter, false, true, BrowserSubmenuStep.Open)]
    [InlineData(BrowserMenuKey.Activate, Key.Space, false, true, BrowserSubmenuStep.Open)]
    [InlineData(BrowserMenuKey.Activate, Key.Enter, false, false, BrowserSubmenuStep.None)]
    [InlineData(BrowserMenuKey.Activate, Key.Enter, true, false, BrowserSubmenuStep.None)]
    public void Submenu_RightOpens_LeftOrEscGoBack(BrowserMenuKey decision, Key key, bool inSubmenu, bool onSubmenuRow, BrowserSubmenuStep expected)
        => Assert.Equal(expected, BrowserMenuKeys.Submenu(decision, key, inSubmenu, onSubmenuRow));

    // Vinda da página (thread do CEF), a seta → com o menu aberto já é NextInGroup: chega ao mesmo passo.
    [Fact]
    public void Submenu_FromPage_SameDecision()
    {
        Assert.Equal(BrowserSubmenuStep.Open, BrowserMenuKeys.Submenu(BrowserMenuKeys.FromPage(Key.Right, false, false, false, false), Key.Right, false, true));
        Assert.Equal(BrowserSubmenuStep.Back, BrowserMenuKeys.Submenu(BrowserMenuKeys.FromPage(Key.Escape, false, false, false, false), Key.Escape, true, false));
        Assert.Equal(BrowserSubmenuStep.Back, BrowserMenuKeys.Submenu(BrowserMenuKeys.FromPage(Key.Left, false, false, false, false), Key.Left, true, false));
    }

    // Leitor de ecrã: a linha é um «item de menu» com ExpandCollapse (fechado/aberto) e conta na posição «n de N»; as
    // cinco entradas do submenu são «1 de 5» … «5 de 5» (o «Voltar» não conta).
    [Fact]
    public void Automation_SubmenuRowExpandCollapse_EntriesNumbered()
    {
        var (type, collapsed, expanded, position, size, entryPosition, entrySize) = OnSta(() =>
        {
            var row = new BrowserMenuSubmenuButton();
            var rows = new List<Button> { new BrowserMenuItemButton(), row, new BrowserMenuItemButton() };
            MainWindow.NumberBrowserMenuItems(rows);
            var peer = UIElementAutomationPeer.CreatePeerForElement(row);
            var pattern = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
            var before = pattern.ExpandCollapseState;
            row.IsExpanded = true;
            var after = pattern.ExpandCollapseState;
            var sub = new List<Button> { new Button() };
            sub.AddRange(Enumerable.Range(0, 5).Select(_ => (Button)new BrowserMenuItemButton()));
            MainWindow.NumberBrowserMenuItems(sub);
            return (peer.GetAutomationControlType(), before, after, AutomationProperties.GetPositionInSet(row), AutomationProperties.GetSizeOfSet(row),
                AutomationProperties.GetPositionInSet(sub[5]), AutomationProperties.GetSizeOfSet(sub[5]));
        });
        Assert.Equal(AutomationControlType.MenuItem, type);
        Assert.Equal(ExpandCollapseState.Collapsed, collapsed);
        Assert.Equal(ExpandCollapseState.Expanded, expanded);
        Assert.Equal(2, position);
        Assert.Equal(3, size);
        Assert.Equal(5, entryPosition);
        Assert.Equal(5, entrySize);
    }

    // A linha nova fica logo a seguir a «Favoritos», em todas as janelas (também na privada), e as teclas do submenu
    // passam pelo mesmo passo vindas da janela e da página.
    [Fact]
    public void Source_RowAfterBookmarks_KeysThroughSubmenuStep()
    {
        var src = Path.Combine(Root(), "src", "Eteris.Browser");
        var menu = File.ReadAllText(Path.Combine(src, "MainWindow.BrowserMenu.cs")).Replace("\r\n", "\n");
        var favorites = menu.IndexOf("Row(\"\\uE734\", \"Favoritos\"", StringComparison.Ordinal);
        var vault = menu.IndexOf("SubmenuRow(", favorites, StringComparison.Ordinal);
        var downloads = menu.IndexOf("\"Transferências\"", favorites, StringComparison.Ordinal);
        Assert.True(favorites > 0 && vault > favorites && vault < downloads);
        Assert.Equal(2, CountOf(menu, "if (BrowserMenuSubmenuStep("));
        var vaultSource = File.ReadAllText(Path.Combine(src, "MainWindow.Vault.cs"));
        Assert.Contains("VaultPages.OpensInNormalWindow(IsPrivate)", vaultSource);
        // A paleta tem os mesmos atalhos.
        Assert.Contains("VaultPages.Entries", File.ReadAllText(Path.Combine(src, "MainWindow.Palette.cs")));
    }

    // Os interruptores novos: oferecer guardar e sugestões ligados; «Pedir o Windows Hello…» desligado para as
    // palavras-passe e ligado para pagamentos e documentos (D-5); as sugestões de cada secção ligadas.
    [Fact]
    public void Settings_Defaults()
    {
        var s = new UserSettings();
        Assert.True(s.PasswordOffer);
        Assert.True(s.PasswordSuggestions);
        Assert.False(s.PasswordAskHello);
        Assert.True(s.PaymentsAskHello);
        Assert.True(s.AutofillPayments);
        Assert.True(s.AutofillContacts);
        Assert.True(s.AutofillDocuments);
        Assert.True(s.AutofillTravel);
        Assert.Equal(ReauthPolicy.DefaultAskWhenFillingPasswords, s.PasswordAskHello);
        Assert.Equal(ReauthPolicy.DefaultAskWhenFillingPayments, s.PaymentsAskHello);
    }

    [Theory]
    [InlineData("pwOffer")]
    [InlineData("pwSuggest")]
    [InlineData("pwAskHello")]
    [InlineData("payAskHello")]
    [InlineData("afPayments")]
    [InlineData("afContacts")]
    [InlineData("afDocuments")]
    [InlineData("afTravel")]
    public void Settings_NewKeysAcceptOnlyBooleans(string key)
    {
        var s = new UserSettings();
        Assert.NotNull(s.With(key, "false"));
        Assert.NotNull(s.With(key, "true"));
        Assert.Null(s.With(key, "talvez"));
        Assert.NotEqual(s.With(key, "false"), s.With(key, "true"));
    }

    // «Pedir o Windows Hello…» é deste computador: nunca viaja. As escolhas de oferecer e sugerir viajam.
    [Fact]
    public void Settings_AskHelloStaysOnThisComputer()
    {
        Assert.DoesNotContain("pwAskHello", Collections.SyncableSettings);
        Assert.DoesNotContain("payAskHello", Collections.SyncableSettings);
        foreach (var key in new[] { "pwOffer", "pwSuggest", "afPayments", "afContacts", "afDocuments", "afTravel" })
            Assert.Contains(key, Collections.SyncableSettings);
    }

    // Desligar «Pedir o Windows Hello ao preencher pagamentos» também pede o Hello: a página das Definições (rota
    // genérica, sem reautenticação) não o pode fazer; só eteris://autofill, que pede.
    [Fact]
    public void Settings_TurningOffPaymentsHello_NeedsReauth()
    {
        Assert.True(UserSettings.NeedsReauth("payAskHello", "false"));
        Assert.False(UserSettings.NeedsReauth("payAskHello", "true"));
        // Revisão: desligar o das palavras-passe também pede (senão anulava-se com um clique num PC desbloqueado).
        Assert.True(UserSettings.NeedsReauth("pwAskHello", "false"));
        Assert.False(UserSettings.NeedsReauth("pwAskHello", "true"));
        Assert.False(UserSettings.NeedsReauth("engine", "brave"));
        Assert.False(SettingsPage.CanSetFromPage("payAskHello", "false"));
        Assert.True(SettingsPage.CanSetFromPage("payAskHello", "true"));
        Assert.True(SettingsPage.CanSetFromPage("pwOffer", "false"));
        // Nas Definições, desligar o das palavras-passe passa pela confirmação do Windows (e a página volta a ligar o
        // interruptor se não for confirmado).
        var routes = File.ReadAllText(Path.Combine(Eteris.Browser.Tests.Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("Data.UserSettings.NeedsReauth(guarded, off)", routes);
        Assert.Contains("Vault.ReauthPurpose.ChangePasswordSetting", routes);
        var html = SettingsPage.Render(new UserSettings());
        Assert.Contains("if (reply !== 'ok') input.checked = true;", html);
    }

    // A secção das Definições: as cinco ligações e os interruptores gerais, com os valores atuais.
    [Fact]
    public void SettingsPage_SectionWithLinksAndSwitches()
    {
        var html = SettingsPage.Render(new UserSettings { PasswordOffer = false, PasswordAskHello = true });
        Assert.Contains("<section id=\"palavras-passe\">", html);
        Assert.Contains("href=\"#palavras-passe\"", html);
        foreach (var entry in VaultPages.Entries) Assert.Contains($"href=\"{entry.Url}\"", html);
        foreach (var key in new[] { "pwOffer", "pwSuggest", "pwAskHello" }) Assert.Contains($"data-key=\"{key}\"", html);
        Assert.Contains("\"pwOffer\":false", html);
        Assert.Contains("\"pwAskHello\":true", html);
        Assert.Contains("\"pwSuggest\":true", html);
        // O resto da folha de estilo não se perde por uma marca solta (era «>>>» antes da regra .syncform .filename).
        Assert.DoesNotContain("\n>>>", html.Replace("\r\n", "\n"));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // Revisão (leitor de ecrã): cada linha da lista de sugestões é um «item de lista» com nome, posição e SelectionItem;
    // a lista anuncia-se ao abrir e a linha escolhida a cada ↑/↓; a bolha de guardar é um painel com a pergunta.
    [Fact]
    public void Automation_SuggestionRows_AndTheSaveBubble()
    {
        var (type, selectedBefore, selectedAfter, name) = OnSta(() =>
        {
            var row = new SuggestionRowElement();
            AutomationProperties.SetName(row, "ana@exemplo.test, conta.exemplo.test");
            var peer = UIElementAutomationPeer.CreatePeerForElement(row);
            var pattern = (ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem);
            var before = pattern.IsSelected;
            row.IsChosen = true;
            return (peer.GetAutomationControlType(), before, pattern.IsSelected, peer.GetName());
        });
        Assert.Equal(AutomationControlType.ListItem, type);
        Assert.False(selectedBefore);
        Assert.True(selectedAfter);
        Assert.Equal("ana@exemplo.test, conta.exemplo.test", name);
        var r = new SuggestionRow("1", "ana@exemplo.test", "conta.exemplo.test", false, OriginMatch.Exact);
        Assert.Equal("ana@exemplo.test, conta.exemplo.test, 2 de 3", SuggestionList.Announcement(r, 1, 3));
        Assert.StartsWith("Palavras-passe guardadas: 3 sugestões.", SuggestionList.Opening("Palavras-passe guardadas", 3));
        Assert.StartsWith("Cartões guardados: 1 sugestão.", SuggestionList.Opening("Cartões guardados", 1));

        var update = new SaveOffer(SaveOfferKind.Update, "https://conta.exemplo.test", "ana", "x", "id1");
        var save = new SaveOffer(SaveOfferKind.Save, "https://conta.exemplo.test", "ana", "x", null);
        Assert.Equal("Atualizar a palavra-passe…", SavePolicy.KeyTip(update));
        Assert.Equal("Guardar a palavra-passe…", SavePolicy.KeyTip(save));
        Assert.Equal("Guardar a palavra-passe de conta.exemplo.test? F6 para responder.", SavePolicy.Announcement(save));
        var window = File.ReadAllText(Path.Combine(Eteris.Browser.Tests.Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.xaml.cs"));
        Assert.Contains("if (!FocusSaveBubble()) FocusOmnibox();", window);
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

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
