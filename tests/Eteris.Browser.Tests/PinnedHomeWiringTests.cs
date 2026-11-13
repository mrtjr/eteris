using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Eteris.Browser.Tests;

// A casa das abas fixadas (Tabs/PinnedHome.cs, MainWindow.PinnedHome.cs) ligada à janela: grava-se com a sessão deste
// computador (nunca a de uma janela privada) e nunca sai dele (não há sincronização de abas).
public class PinnedHomeWiringTests
{
    // Sem isto, «Usar este endereço como casa» só ficava gravado na navegação seguinte.
    [Fact]
    public void HomeChanges_SaveTheSession()
        => Assert.Matches(@"private void OnTabPropertyChanged\(object\? sender, PropertyChangedEventArgs e\)\s*\{\s*if \(e\.PropertyName is [^;]*nameof\(BrowserTab\.Home\)\) ScheduleSessionSave\(\);",
            Source("MainWindow.xaml.cs"));

    // A casa de uma aba privada só vive em memória: a gravação sai logo numa privada, a sessão só lê as janelas normais e as
    // janelas fechadas (que levam os metas) não guardam as privadas.
    [Fact]
    public void PrivateWindows_NeverWriteTheHome()
    {
        var session = Source("MainWindow.Session.cs");
        Assert.Matches(@"private void ScheduleSessionSave\(\)\s*\{\s*if \(IsPrivate\) return;", session);
        Assert.Matches(@"private static void SaveSessionNow\(bool now = false\)\s*\{[^}]*\}[^}]*NormalWindows\(\)", session);
        Assert.Contains("if (IsPrivate || others == 0) return;", Source("MainWindow.ClosedWindows.cs"));
    }

    // Não há sincronização de abas no Éteris: a casa (um endereço pode trazer tokens) fica na sessão deste computador.
    [Fact]
    public void TheHome_NeverLeavesThisPc()
    {
        var sync = Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser", "Sync"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Sync"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sync);
        Assert.All(sync, f => Assert.DoesNotMatch(@"\bHome\b", File.ReadAllText(f)));
        Assert.Contains("As abas abertas NÃO vão", Source(Path.Combine("Sync", "ProfileTransfer.cs")));
    }

    // Alt+Home, o gesto de «página inicial» do Chrome, do Firefox e do Vivaldi: vale com o foco na moldura e na página
    // (o mesmo Shortcut()), junto de Alt+←/→.
    [Fact]
    public void AltHome_GoesHome_FromTheFrameAndThePage()
    {
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("if (alt && !ctrl && !shift && key == Key.Home) return GoHomeActive;", code);
        Assert.Matches(@"if \(alt && key == Key\.Right\) return GoForward;\s*(//[^\n]*\n\s*)*if \(alt && !ctrl && !shift && key == Key\.Home\) return GoHomeActive;", code);
    }

    // O clique no ícone só volta a casa na aba que já era a ativa (o 1.º clique numa fixada de fundo só a ativa), ao largar
    // o botão e nunca depois de um arrasto; Ctrl/Shift+clique (multi-seleção) e os botões da aba nunca o armam.
    [Fact]
    public void TheIconClick_IsArmedBeforeActivating_AndNeverAfterADrag()
    {
        var down = Body(Source("MainWindow.xaml.cs"), "private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)");
        var arm = down.IndexOf("_homeClick = ReferenceEquals(Tabs.Active, tab)", StringComparison.Ordinal);
        Assert.True(arm > down.IndexOf("HandleSelectionClick(tab)", StringComparison.Ordinal), "arma depois da multi-seleção");
        Assert.True(arm < down.IndexOf("Tabs.Active = tab;", StringComparison.Ordinal), "arma antes de ativar");
        Assert.Contains("tab.IsAwayFromHome && IsInsideIconHit(e.OriginalSource as DependencyObject, (FrameworkElement)sender)", down);

        var up = Body(Source("MainWindow.TabDrag.cs"), "private void TabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)");
        Assert.Matches(@"ResetTabDrag\(\);\s*(//[^\n]*\n\s*)*if \(!dropped\) TryGoHomeFromIcon\(e\);", up);

        var side = Load().Descendants(Wpf + "ListBox").Single(e => (string?)e.Attribute(X + "Name") == "SideTabList");
        Assert.Equal("SideTabList_PreviewMouseLeftButtonUp", (string?)side.Attribute("PreviewMouseLeftButtonUp"));

        var home = Source("MainWindow.PinnedHome.cs");
        // Ao largar: a mesma aba, ainda ativa e fora de casa, e o ponteiro ainda dentro da área do ícone dela.
        Assert.Contains("ReferenceEquals(tab, Tabs.Active) && tab.IsAwayFromHome", home);
        Assert.Contains("InputHitTest(e.GetPosition(list))", home);
    }

    // Nos dois modelos de aba: o ponto no canto superior direito do ícone (o da nota fica em baixo), o ⌂ que aparece ao pairar
    // sobre o ícone da aba ativa fora de casa, e as cores do sistema em alto contraste.
    [Theory]
    [InlineData("TabItemStyle", "TabRoot")]
    [InlineData("SideTabItemStyle", "IconHit")]
    public void BothTabTemplates_HaveTheHomeDot(string style, string hit)
    {
        var template = Style(style);
        Assert.Single(Named(template, "IconBox"));
        var dot = Assert.Single(Named(template, "HomeDot"));
        Assert.Equal("Ellipse", dot.Name.LocalName);
        Assert.Equal("Right", (string?)dot.Attribute("HorizontalAlignment"));
        Assert.Equal("Top", (string?)dot.Attribute("VerticalAlignment"));
        Assert.Equal("False", (string?)dot.Attribute("IsHitTestVisible"));
        Assert.Equal("Collapsed", (string?)dot.Attribute("Visibility"));
        Assert.Equal("{DynamicResource HomeDotBrush}", (string?)dot.Attribute("Fill"));
        // O ponto e o ⌂ estão dentro da caixa do ícone, depois de tudo o que ela mostra (ficam por cima do Spinner).
        var box = Assert.Single(Named(template, "IconBox"));
        Assert.Equal(["HomeDot", "HomeGlyph"], box.Elements().TakeLast(2).Select(e => (string?)e.Attribute(X + "Name")));
        Assert.Single(Named(template, hit));

        var triggers = template.Descendants(Wpf + "ControlTemplate.Triggers").Single().Elements().ToList();
        var shows = triggers.FindIndex(t => t.Name == Wpf + "DataTrigger" && (string?)t.Attribute("Binding") == "{Binding IsAwayFromHome}"
            && Setter(t, "HomeDot", "Visibility") == "Visible");
        Assert.True(shows >= 0, "o ponto aparece com IsAwayFromHome");

        // O ⌂: aba fora de casa, ativa (a seleção da lista) e com o rato sobre a área do ícone; depois dos gatilhos que mostram
        // o ícone (a carregar, ícone próprio, nova aba), para ganhar a eles.
        var hover = triggers.FindIndex(t => t.Name == Wpf + "MultiDataTrigger" && Setter(t, "HomeGlyph", "Visibility") == "Visible");
        Assert.True(hover >= 0, "o ⌂ ao pairar");
        var conditions = triggers[hover].Descendants(Wpf + "Condition").Select(c => (string?)c.Attribute("Binding")).ToList();
        Assert.Contains("{Binding IsAwayFromHome}", conditions);
        Assert.Contains("{Binding IsSelected, RelativeSource={RelativeSource Self}}", conditions);
        Assert.Contains($"{{Binding IsMouseOver, ElementName={hit}}}", conditions);
        foreach (var icon in new[] { "Favicon", "CustomIcon", "NewTabIcon", "Spinner" })
            Assert.Equal("Collapsed", Setter(triggers[hover], icon, "Visibility"));
        Assert.Equal("Hand", Setter(triggers[hover], hit, "Cursor"));
        foreach (var binding in new[] { "{Binding IsLoading}", "{Binding HasCustomIcon}", "{Binding IsNewTab}" })
            Assert.True(hover > triggers.FindIndex(t => (string?)t.Attribute("Binding") == binding), binding);

        var contrast = triggers.FindIndex(t => (string?)t.Attribute("Binding") == "{Binding Path=(SystemParameters.HighContrast)}");
        Assert.True(contrast > hover, "o alto contraste vem depois, para ganhar também ao ⌂");
        Assert.Equal("{DynamicResource {x:Static SystemColors.HighlightBrushKey}}", Setter(triggers[contrast], "HomeDot", "Fill"));
        Assert.Equal("{DynamicResource {x:Static SystemColors.WindowBrushKey}}", Setter(triggers[contrast], "HomeDot", "Stroke"));
        Assert.Equal("{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}", Setter(triggers[contrast], "HomeGlyph", "Foreground"));
        // O HighlightText só tem contraste garantido sobre o Highlight: o fundo da aba continua a ser o do Éteris (o Aquatic
        // dava #263B50 sobre #2A2F45, 1,15:1), por isso o ⌂ leva o Highlight por trás.
        Assert.Equal("{DynamicResource {x:Static SystemColors.HighlightBrushKey}}", Setter(triggers[contrast], "HomeGlyph", "Background"));
        // Nada se mexe: o ponto não pulsa (o «reduzir movimento» não tem nada a parar).
        Assert.DoesNotContain(template.Descendants(), e => (string?)e.Attribute("Storyboard.TargetName") is "HomeDot" or "HomeGlyph");
    }

    // Nas verticais encolhidas (só ícones) a linha inteira é a área do clique.
    [Fact]
    public void CollapsedSideTabs_UseTheWholeRow()
    {
        var triggers = Style("SideTabItemStyle").Descendants(Wpf + "ControlTemplate.Triggers").Single().Elements().ToList();
        Assert.Contains(triggers, t => t.Name == Wpf + "MultiDataTrigger" && Setter(t, "HomeGlyph", "Visibility") == "Visible"
            && t.Descendants(Wpf + "Condition").Any(c => (string?)c.Attribute("Binding") == "{Binding IsMouseOver, ElementName=Row}")
            && t.Descendants(Wpf + "Condition").Any(c => ((string?)c.Attribute("Binding"))?.StartsWith("{Binding SideCollapsed", StringComparison.Ordinal) == true));
        Assert.Contains("SideCollapsed ? \"Row\" : \"IconHit\"", Source("MainWindow.PinnedHome.cs"));
        // A área do ícone nas verticais abertas: 24×24 à volta do ícone de 16, sem mudar a disposição.
        var hit = Assert.Single(Named(Style("SideTabItemStyle"), "IconHit"));
        Assert.Equal(("24", "24", "-4", "Transparent"), ((string?)hit.Attribute("Width"), (string?)hit.Attribute("Height"),
            (string?)hit.Attribute("Margin"), (string?)hit.Attribute("Background")));
    }

    // O cartão da aba diz «⌂ Voltar a …» (sem um ToolTip próprio no ícone, que tapava o cartão).
    [Fact]
    public void TheTabCard_SaysWhereHomeIs()
    {
        var card = Style("TabCardTip");
        var hint = Assert.Single(Named(card, "HomeHint"));
        Assert.Equal("{Binding HomeHint, StringFormat='⌂ {0} — clique no ícone com a aba aberta, ou Alt+Home'}", (string?)hint.Attribute("Text"));
        Assert.Equal("{DynamicResource TextBrush}", (string?)hint.Attribute("Foreground"));
        var note = Assert.Single(Named(card, "Note"));
        Assert.Same(hint, note.ElementsAfterSelf().First());
        Assert.Contains(card.Descendants(Wpf + "DataTrigger"), t => (string?)t.Attribute("Binding") == "{Binding IsAwayFromHome}"
            && Setter(t, "HomeHint", "Visibility") == "Visible");
        foreach (var style in new[] { "TabItemStyle", "SideTabItemStyle" })
            Assert.Null(Named(Style(style), "IconBox").Single().Attribute("ToolTip"));
    }

    [Fact]
    public void TabMenuAndPalette_OfferTheHome()
    {
        var menu = Source("MainWindow.TabMenu.cs");
        Assert.Matches(@"\.Item\(""[^""]*"", tab\.IsPinned \? ""Desafixar aba"" : ""Fixar aba"", \(\) => Tabs\.TogglePin\(tab\)\)\s*\.With\(menu => AddHomeItems\(menu, tab\)\)", menu);
        var home = Source("MainWindow.PinnedHome.cs");
        Assert.Contains(".Item(\"\\uE80F\", \"Voltar a casa\", () => GoHome(tab), \"Alt+Home\", enabled: tab.IsAwayFromHome)", home);
        Assert.Contains(".Item(\"\\uE8EC\", \"Usar este endereço como casa\", () => UseAddressAsHome(tab), enabled: tab.IsAwayFromHome && PinnedHome.CanBeHome(tab.Address))", home);

        var palette = Source("MainWindow.Palette.cs");
        Assert.Matches(@"""Radar de abas""[^\n]*\n\s*foreach \(var command in HomeCommands\(tab\)\) yield return command;", palette);
        // Sem «à esquerda»: com as abas verticais, a fixada vai para o topo.
        Assert.Contains("(\"\\uE718\", \"Fixar esta aba\", \"Fica no início da lista de abas, com casa neste endereço\", null, () => Tabs.TogglePin(tab))", home);
        Assert.Contains("(\"\\uE77A\", \"Desafixar esta aba\", \"Deixa de ter casa\", null, () => Tabs.TogglePin(tab))", home);
        Assert.Contains("(\"\\uE80F\", \"Voltar a casa\", tab.HomeHint!, \"Alt+Home\", GoHomeActive)", home);
        Assert.Contains("(\"\\uE8EC\", \"Usar este endereço como casa\", $\"A aba passa a voltar a {PinnedHome.Label(tab.Address)}\", null, () => UseAddressAsHome(tab))", home);
    }

    // Os avisos do Alt+Home e o «Anular» de «Usar este endereço como casa» (que entra na pilha com o endereço anterior, para o
    // «Triturar» a limpar).
    [Fact]
    public void GoingHome_TellsWhyNot_AndUseAsHome_CanBeUndone()
    {
        var home = Source("MainWindow.PinnedHome.cs");
        Assert.Contains("\"Já está na casa desta aba\"", home);
        Assert.Contains("\"Esta aba fixada ainda não tem casa: a primeira página web que abrir passa a sê-lo\"", home);
        // A paleta só tem «Fixar esta aba» numa página web (PinnedHome.CanBeHome): na nova aba e nas páginas do Éteris, o
        // aviso não manda para um comando que lá não está.
        Assert.Matches(@"if \(!tab\.IsPinned\)\s*ShowToast\(PinnedHome\.CanBeHome\(tab\.Address\)\s*\? ""Só as abas fixadas têm casa: Ctrl\+K › «Fixar esta aba»""\s*: ""Só as abas fixadas têm casa: abra um site e depois Ctrl\+K › «Fixar esta aba»""\);", home);
        Assert.Matches(@"if \(PinnedHome\.CanBeHome\(tab\.Address\)\)\s*yield return new\(""\\uE718"", ""Fixar esta aba""", home);
        Assert.Contains("ShowUndoToast($\"A casa desta aba passa a ser {tab.HomeLabel}\", $\"Casa nova: «{tab.HomeLabel}»\"", home);
        Assert.Contains("previous is null ? [] : [previous]", home);
        // Adormecida, acorda já na casa (uma só carga); acordada, pelo LoadUrl (omnibox, motor de vídeo, Voltar).
        Assert.Matches(@"if \(tab\.IsSleeping\)\s*\{[^}]*tab\.Address = home;\s*tab\.BeginGoingHome\(\);\s*Tabs\.Active = tab;", home);
        Assert.Matches(@"if \(!ReferenceEquals\(Tabs\.Active, tab\)\) Tabs\.Active = tab;\s*tab\.BeginGoingHome\(\);\s*LoadUrl\(home\);", home);
    }

    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load() => XDocument.Parse(Source("MainWindow.xaml"));

    private static XElement Style(string key) => Load().Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(X + "Key") == key);

    private static IEnumerable<XElement> Named(XElement root, string name) => root.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name);

    private static string? Setter(XElement trigger, string target, string property)
        => (string?)trigger.Elements(Wpf + "Setter").FirstOrDefault(s => (string?)s.Attribute("TargetName") == target
            && (string?)s.Attribute("Property") == property)?.Attribute("Value");

    // O corpo de um método (até à chaveta que fecha no mesmo recuo).
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = Regex.Match(code[start..], @"\r?\n    \}").Index;
        return code.Substring(start, end);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative)
    {
        var path = Path.Combine(Root, "src", "Eteris.Browser", relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
