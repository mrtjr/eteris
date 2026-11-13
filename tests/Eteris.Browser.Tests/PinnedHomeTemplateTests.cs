using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Os dois modelos de aba verdadeiros (lidos do MainWindow.xaml, sem os tratadores de eventos e com os recursos estáticos
// substituídos por simples) carregam e mostram o ponto «fora de casa» só quando a aba fixada sai de casa: os gatilhos
// (incluindo o do alto contraste, uma ligação a SystemParameters, e os ElementName do ⌂) não falham ao aplicar o modelo.
[Collection(WpfCollection.Name)]
public class PinnedHomeTemplateTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("TabItemStyle")]
    [InlineData("SideTabItemStyle")]
    public void TheDot_ShowsOnlyAwayFromHome(string style)
    {
        var xaml = Resources(style);
        var states = StaRunner.Run(() =>
        {
            var resources = (ResourceDictionary)XamlReader.Parse(xaml);
            var tab = new BrowserTab("https://exemplo.pt/caixa") { IsPinned = true, IsSleeping = true, IsLoading = true };
            var list = new ListBox { ItemContainerStyle = (Style)resources[style], ItemsSource = new[] { tab } };
            var window = new Window
            {
                Content = list, Width = 400, Height = 200, Left = -32000, Top = -32000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(tab);
                Visibility Dot() => ((FrameworkElement)item.Template.FindName("HomeDot", item)).Visibility;
                Visibility Glyph() => ((FrameworkElement)item.Template.FindName("HomeGlyph", item)).Visibility;
                var atHome = Dot();
                tab.Address = "https://exemplo.pt/outra";
                window.UpdateLayout();
                var away = (Dot(), Glyph());
                tab.Address = "https://exemplo.pt/caixa#topo";
                window.UpdateLayout();
                var anchor = Dot();
                return (atHome, away, anchor);
            }
            finally { window.Close(); }
        }, nameof(PinnedHomeTemplateTests) + "." + nameof(TheDot_ShowsOnlyAwayFromHome) + "." + style);

        Assert.Equal(Visibility.Collapsed, states.atHome);
        // A dormir e a carregar, o ponto vê-se; o ⌂ só ao pairar sobre o ícone da aba ativa.
        Assert.Equal((Visibility.Visible, Visibility.Collapsed), states.away);
        Assert.Equal(Visibility.Collapsed, states.anchor);
    }

    // Um dicionário com o cartão da aba e o modelo pedido, sem tratadores de eventos (precisam do código da janela) e com
    // os recursos estáticos que vêm do tema substituídos por simples.
    private static string Resources(string style)
    {
        var doc = XDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.xaml")));
        XElement Style(string key) => new(doc.Descendants(Wpf + "Style").Single(s => (string?)s.Attribute(X + "Key") == key));
        var styles = new[] { Style("TabCardTip"), Style(style) };
        foreach (var element in styles.SelectMany(s => s.DescendantsAndSelf()).ToList())
        {
            if (element.Name == Wpf + "EventSetter") { element.Remove(); continue; }
            foreach (var attribute in element.Attributes().Where(a => Regex.IsMatch(a.Value, @"^[A-Za-z]+_[A-Za-z]+$")).ToList()) attribute.Remove();
        }
        var dictionary = new XElement(Wpf + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "shell", "clr-namespace:System.Windows.Shell;assembly=PresentationFramework"),
            XElement.Parse($"<FontFamily xmlns=\"{Wpf}\" xmlns:x=\"{X}\" x:Key=\"IconFont\">Segoe UI</FontFamily>"),
            XElement.Parse($"<FontFamily xmlns=\"{Wpf}\" xmlns:x=\"{X}\" x:Key=\"EmojiFont\">Segoe UI</FontFamily>"),
            XElement.Parse($"<Style xmlns=\"{Wpf}\" xmlns:x=\"{X}\" x:Key=\"SpinnerArc\" TargetType=\"Path\" />"),
            XElement.Parse($"<Style xmlns=\"{Wpf}\" xmlns:x=\"{X}\" x:Key=\"TabCloseButton\" TargetType=\"Button\" />"),
            styles);
        return dictionary.ToString();
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
