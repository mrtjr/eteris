using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Eteris.Browser.Browser;
using Eteris.Browser.Downloads;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Palette;
using Eteris.Browser.Permissions;
using Eteris.Browser.Sidebar;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Defeito visto com o leitor de ecrã: as linhas da paleta diziam «PaletteCommand { Glyph = …, Run = System.Action }» e as
// abas «Eteris.Browser.Tabs.BrowserTab». O WPF dá a cada item de uma lista (ListBoxItem, ou o ContentPresenter de um
// ItemsControl) um AutomationPeer cujo nome é o AutomationProperties.Name do contentor; vazio, cai no ToString() do item.
// Um nome posto dentro do DataTemplate (num Border, como nos chips dos Espaços) não chega ao item. Por isso cada lista
// com dados dá o nome no estilo do contentor (ItemContainerStyle), ligado a uma propriedade legível do item.
[Collection(WpfCollection.Name)]
public class ListItemNamesTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] XamlFiles =
    [
        "MainWindow.xaml",
        "TabRadarView.xaml",
        "ClientCertificateDialog.xaml",
    ];

    // Todas as listas com dados das janelas (x:Name → caminho ligado ao nome; «A+B» é um MultiBinding). Uma lista nova
    // sem nome no contentor faz falhar o teste de varrimento.
    private static readonly Dictionary<string, string> ExpectedNames = new()
    {
        ["MainWindow.xaml#PaletteList"] = "Title",
        ["MainWindow.xaml#TabList"] = "DisplayTitle",
        ["MainWindow.xaml#SideTabList"] = "DisplayTitle",
        ["MainWindow.xaml#TabListMenuList"] = "Title",
        ["MainWindow.xaml#SuggestionList"] = "Text",
        ["MainWindow.xaml#SpaceStrip"] = "Summary",
        ["MainWindow.xaml#SideSpaceStrip"] = "Summary",
        ["MainWindow.xaml#SidebarLinkList"] = "Title",
        ["MainWindow.xaml#SiteInfoPermissions"] = "Name+State",
        ["MainWindow.xaml#SiteKnowledgeList"] = "Title",
        ["MainWindow.xaml#MediaList"] = "MediaHeadline",
        ["MainWindow.xaml#DownloadsList"] = "FileName",
        ["MainWindow.xaml#ShieldHosts"] = "Key",
        ["TabRadarView.xaml#Results"] = "Title",
        ["ClientCertificateDialog.xaml#List"] = "Text",
    };

    [Fact]
    public void EveryDataList_NamesItsItemsInTheContainerStyle()
    {
        var found = new Dictionary<string, string>();
        foreach (var file in XamlFiles)
        {
            var doc = Load(file);
            foreach (var list in doc.Descendants().Where(e => e.Name == Wpf + "ListBox" || e.Name == Wpf + "ItemsControl"))
            {
                var name = (string?)list.Attribute(X + "Name");
                Assert.False(name is null, $"{file}: uma lista sem x:Name");
                var style = ContainerStyle(doc, list);
                Assert.False(style is null, $"{file}#{name}: sem ItemContainerStyle, o leitor de ecrã diz o ToString() de cada item");
                found[$"{file}#{name}"] = NamePath(style!) ?? "(sem AutomationProperties.Name ligado no contentor)";
            }
        }
        Assert.Equal(ExpectedNames.OrderBy(p => p.Key), found.OrderBy(p => p.Key));
    }

    // Os chips dos Espaços e as linhas da barra lateral tinham o nome num Border dentro do DataTemplate: não chega ao item.
    [Fact]
    public void TemplateNames_AreBackedByTheContainer()
    {
        var doc = Load("MainWindow.xaml");
        var sidebar = doc.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(X + "Key") == "SidebarList");
        Assert.Contains(sidebar.Elements(Wpf + "Setter"), s => (string?)s.Attribute("Property") == "ItemContainerStyle");
        foreach (var strip in new[] { "SpaceStrip", "SideSpaceStrip" })
        {
            var list = doc.Descendants(Wpf + "ItemsControl").Single(e => (string?)e.Attribute(X + "Name") == strip);
            Assert.Equal("{StaticResource SpaceChipContainer}", (string?)list.Attribute("ItemContainerStyle"));
        }
    }

    // As próprias listas também têm nome (antes a da paleta, as das abas e a das sugestões eram «lista» sem nome).
    [Theory]
    [InlineData("PaletteList", "Comandos")]
    [InlineData("TabList", "Abas")]
    [InlineData("SideTabList", "Abas")]
    [InlineData("SuggestionList", "Sugestões")]
    public void Lists_HaveAName(string list, string expected)
    {
        var element = Load("MainWindow.xaml").Descendants(Wpf + "ListBox").Single(e => (string?)e.Attribute(X + "Name") == list);
        Assert.Equal(expected, (string?)element.Attribute("AutomationProperties.Name"));
    }

    public static TheoryData<string, string, string, string?> Lists() => new()
    {
        { "MainWindow.xaml#PaletteList", "palette", "Nova aba", "Abrir a página inicial do Éteris" },
        { "MainWindow.xaml#TabList", "tab", "Exemplo de página", "Fixada · Som silenciado" },
        { "MainWindow.xaml#TabList", "fresh-tab", "Nova aba", "" },
        { "MainWindow.xaml#TabList", "untitled-tab", "exemplo.pt", "" },
        { "MainWindow.xaml#SideTabList", "tab", "Exemplo de página", "Fixada · Som silenciado" },
        { "MainWindow.xaml#TabListMenuList", "radar", "Exemplo de página", "exemplo.pt" },
        { "MainWindow.xaml#SuggestionList", "suggestion", "gatos persas", "Pesquisar no DuckDuckGo" },
        { "MainWindow.xaml#SpaceStrip", "space", "Trabalho · 0 abas", null },
        { "MainWindow.xaml#SideSpaceStrip", "space", "Trabalho · 0 abas", null },
        { "MainWindow.xaml#SidebarLinkList", "link", "Receitas de outono", null },
        { "MainWindow.xaml#SidebarLinkList", "heading", "Hoje", null },
        { "MainWindow.xaml#SiteInfoPermissions", "permission", "Câmara: Bloqueado", null },
        { "MainWindow.xaml#SiteKnowledgeList", "knowledge", "3 cookies", "Guardados por este site" },
        { "MainWindow.xaml#MediaList", "media", "Concerto de Natal", "Coro · exemplo.pt" },
        { "MainWindow.xaml#DownloadsList", "download", "relatorio.pdf", DownloadFormat.Size(2048) },
        { "MainWindow.xaml#ShieldHosts", "host", "rastreador.exemplo", "Pedidos bloqueados: 3" },
        { "TabRadarView.xaml#Results", "radar", "Exemplo de página", "exemplo.pt" },
        { "ClientCertificateDialog.xaml#List", "certificate", "CN=Ana — emitido por CA Exemplo", null },
    };

    // O nome que o AutomationPeer do item dá com os setters de automação do estilo verdadeiro (lidos do XAML) e um item
    // verdadeiro: o caminho ligado tem de existir no tipo, e nunca sai o nome da classe nem o «{ … }» de um record.
    [Theory]
    [MemberData(nameof(Lists))]
    public void ItemPeer_IsNamedLikeTheRow(string list, string item, string expectedName, string? expectedHelp)
    {
        var (file, name) = (list.Split('#')[0], list.Split('#')[1]);
        var doc = Load(file);
        var element = doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);
        var isListBox = element.Name == Wpf + "ListBox";
        // Sem estilo de contentor, um estilo vazio: o item fica com o nome que o WPF lhe der (o ToString()).
        var style = ContainerStyle(doc, element) ?? new XElement(Wpf + "Style", new XAttribute("TargetType", isListBox ? "ListBoxItem" : "ContentPresenter"));
        var automationStyle = AutomationOnly(style);

        var (peerName, peerHelp, peerType) = OnSta(() =>
        {
            ItemsControl control = isListBox ? new ListBox() : new ItemsControl();
            control.ItemContainerStyle = (Style)XamlReader.Parse(automationStyle);
            control.ItemsSource = new[] { Item(item) };
            var window = new Window
            {
                Content = control, Width = 300, Height = 200, Left = -32000, Top = -32000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                var peer = UIElementAutomationPeer.CreatePeerForElement(control);
                var row = Assert.Single(peer.GetChildren());
                return (row.GetName(), row.GetHelpText(), row.GetAutomationControlType());
            }
            finally { window.Close(); }
        });

        Assert.Equal(expectedName, peerName);
        Assert.DoesNotContain("{", peerName);
        Assert.DoesNotContain("Eteris.", peerName);
        if (expectedHelp is not null) Assert.Equal(expectedHelp, peerHelp);
        Assert.Equal(isListBox ? AutomationControlType.ListItem : AutomationControlType.DataItem, peerType);
    }

    // A paleta diz também o atalho de cada comando (a etiqueta à direita da linha).
    [Fact]
    public void PaletteRows_ExposeTheShortcutAsAcceleratorKey()
    {
        var style = PaletteStyle();
        Assert.Contains(style.Elements(Wpf + "Setter"), s => (string?)s.Attribute("Property") == "AutomationProperties.AcceleratorKey"
            && (string?)s.Attribute("Value") == "{Binding Shortcut}");
        var key = OnSta(() =>
        {
            var control = new ListBox { ItemContainerStyle = (Style)XamlReader.Parse(AutomationOnly(style)), ItemsSource = new[] { Item("palette") } };
            var window = new Window { Content = control, Width = 300, Height = 200, Left = -32000, Top = -32000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                window.UpdateLayout();
                return Assert.Single(UIElementAutomationPeer.CreatePeerForElement(control).GetChildren()).GetAcceleratorKey();
            }
            finally { window.Close(); }
        });
        Assert.Equal("Ctrl+T", key);
    }

    private static XElement PaletteStyle()
    {
        var doc = Load("MainWindow.xaml");
        return doc.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(X + "Key") == "PaletteItemStyle");
    }

    private static object Item(string kind) => kind switch
    {
        "palette" => new PaletteCommand("\uE710", "Nova aba", "Abrir a página inicial do Éteris", "Ctrl+T", () => { }),
        "tab" => Tab(t => { t.Title = "Exemplo de página"; t.IsPinned = true; t.IsMuted = true; }),
        "fresh-tab" => new BrowserTab("about:blank"),
        "untitled-tab" => Tab(t => t.Title = ""),
        "radar" => new RadarRow(Tab(t => t.Title = "Exemplo de página"), null, "Exemplo de página", "https://exemplo.pt/",
            "exemplo.pt", "E", Brushes.Gray, null, "Em uso"),
        "suggestion" => new Suggestion(SuggestionKind.Search, "gatos persas", "Pesquisar no DuckDuckGo", "https://duckduckgo.com/?q=gatos"),
        "space" => new Workspace("Trabalho"),
        "link" => new SidebarLink("https://exemplo.pt/receitas", "Receitas de outono", "exemplo.pt", "há 2 horas", false),
        "heading" => new SidebarHeading("Hoje"),
        "permission" => Private(typeof(MainWindow), "PermissionRow", SitePermission.Camera, "Câmara", "Bloqueado", Brushes.Gray),
        "knowledge" => new SiteKnowledge.Line("\uE7BA", "3 cookies", "Guardados por este site"),
        "media" => Tab(t => { t.Title = "Página do coro"; t.MediaTitle = "Concerto de Natal"; t.MediaArtist = "Coro"; }),
        "download" => Download(),
        "host" => new KeyValuePair<string, int>("rastreador.exemplo", 3),
        "certificate" => Private(typeof(ClientCertificateDialog), "Item", "CN=Ana — emitido por CA Exemplo", "AB12"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static BrowserTab Tab(Action<BrowserTab> set)
    {
        var tab = new BrowserTab("https://www.exemplo.pt/pagina");
        set(tab);
        return tab;
    }

    private static DownloadEntry Download()
    {
        var entry = new DownloadEntry(1, @"C:\Transferencias\relatorio.pdf", "relatorio.pdf");
        entry.Received = 2048;
        entry.IsComplete = true;
        return entry;
    }

    private static object Private(Type owner, string type, params object[] arguments)
    {
        var nested = owner.GetNestedType(type, System.Reflection.BindingFlags.NonPublic)!;
        return Activator.CreateInstance(nested, arguments)!;
    }

    // O estilo dos contentores de uma lista: inline (<X.ItemContainerStyle>), por chave ({StaticResource …}) ou dado pelo
    // estilo da própria lista (Setter de ItemContainerStyle, como o SidebarList).
    private static XElement? ContainerStyle(XDocument doc, XElement list)
    {
        if (list.Element(list.Name + ".ItemContainerStyle")?.Element(Wpf + "Style") is { } inline) return inline;
        if (ByKey(doc, (string?)list.Attribute("ItemContainerStyle")) is { } keyed) return keyed;
        if (ByKey(doc, (string?)list.Attribute("Style")) is { } listStyle
            && listStyle.Elements(Wpf + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == "ItemContainerStyle") is { } setter)
            return setter.Element(Wpf + "Setter.Value")?.Element(Wpf + "Style") ?? ByKey(doc, (string?)setter.Attribute("Value"));
        return null;
    }

    private static XElement? ByKey(XDocument doc, string? reference)
    {
        if (reference is null || !reference.StartsWith("{StaticResource ", StringComparison.Ordinal)) return null;
        var key = reference["{StaticResource ".Length..^1].Trim();
        return doc.Descendants(Wpf + "Style").SingleOrDefault(s => (string?)s.Attribute(X + "Key") == key);
    }

    // «Title» para Value="{Binding Title}"; «Name+State» para um MultiBinding.
    private static string? NamePath(XElement style)
    {
        var setter = style.Elements(Wpf + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == "AutomationProperties.Name");
        if (setter is null) return null;
        if ((string?)setter.Attribute("Value") is { } value)
            return value.StartsWith("{Binding ", StringComparison.Ordinal) ? value["{Binding ".Length..^1].Split(',')[0].Trim() : null;
        var multi = setter.Element(Wpf + "Setter.Value")?.Element(Wpf + "MultiBinding");
        return multi is null ? null : string.Join("+", multi.Elements(Wpf + "Binding").Select(b => (string?)b.Attribute("Path")));
    }

    // Só os setters AutomationProperties.* do estilo verdadeiro, num Style que o XamlReader lê sem a janela (sem
    // EventSetters, recursos nem moldes).
    private static string AutomationOnly(XElement style)
    {
        var copy = new XElement(Wpf + "Style", new XAttribute("TargetType", (string)style.Attribute("TargetType")!),
            style.Elements(Wpf + "Setter")
                .Where(s => ((string?)s.Attribute("Property") ?? "").StartsWith("AutomationProperties.", StringComparison.Ordinal))
                .Select(s => new XElement(s)));
        return copy.ToString();
    }

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(Root(), "src", "Eteris.Browser", file));

    private static T OnSta<T>(Func<T> work, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => StaRunner.Run(work, nameof(ListItemNamesTests) + "." + test);

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
