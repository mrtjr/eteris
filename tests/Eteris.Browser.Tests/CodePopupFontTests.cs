using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// Captura do dono: no menu do navegador (Alt+F) o título «Éteris», o subtítulo e «Zoom» saíam em quadrados. O Popup é
// criado em código sem pai, herda as propriedades do PlacementTarget (o MenuButton, com a fonte de ícones) e o texto sem
// fonte própria herdava a Segoe Fluent Icons, que não tem letras.
[Collection(WpfCollection.Name)]
public class CodePopupFontTests
{
    private static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    [Fact]
    public void ParentlessPopupOnIconButton_InheritsIconFont_UnlessTheRootFixesUiFont()
    {
        var (plain, fixedText) = OnSta(() =>
        {
            var plainText = new TextBlock { Text = "Éteris" };
            var plain = OpenOnIconButton(new Border { Child = plainText });
            var text = new TextBlock { Text = "Éteris" };
            var fixedPopup = OpenOnIconButton(CodePopup.WithUiFont(new Border { Child = text }));
            var result = (plainText.FontFamily.Source, text.FontFamily.Source);
            plain.IsOpen = false;
            fixedPopup.IsOpen = false;
            return result;
        });
        // Sem a correção o defeito reproduz-se (prova que o teste olha para o sítio certo)...
        Assert.Equal(IconFont.Source, plain);
        // ...e com a raiz preparada o texto fica com a fonte da interface.
        Assert.Equal(UiFont.Source, fixedText);
    }

    [Theory]
    [InlineData(false, "MENU DO NAVEGADOR")]
    [InlineData(true, "JANELA PRIVADA")]
    public void BrowserMenu_HeadingAndZoomLabel_UseUiFont_GlyphsKeepIconFont(bool isPrivate, string subtitle)
    {
        var fonts = OnSta(() =>
        {
            var content = new DockPanel();
            var heading = MainWindow.BrowserMenuHeading(isPrivate);
            DockPanel.SetDock(heading, Dock.Top);
            content.Children.Add(heading);
            content.Children.Add(MainWindow.BrowserMenuLabel("", "Zoom"));
            var popup = OpenOnIconButton(MainWindow.BrowserMenuFrame(content, 400));
            popup.UpdateLayout();
            var map = Texts(content).ToDictionary(t => t.Text, t => t.FontFamily.Source);
            popup.IsOpen = false;
            return map;
        });
        Assert.Equal(UiFont.Source, fonts["Éteris"]);
        Assert.Equal(UiFont.Source, fonts[subtitle]);
        Assert.Equal(UiFont.Source, fonts["Zoom"]);
        Assert.Equal(IconFont.Source, fonts[""]);
    }

    [Fact]
    public void CodePopups_PrepareTheirRootWithUiFont()
    {
        var src = Path.Combine(Root(), "src", "Eteris.Browser");
        Assert.Contains("var frame = BrowserMenuFrame(content,", File.ReadAllText(Path.Combine(src, "MainWindow.BrowserMenu.cs")));
        Assert.Contains("CodePopup.WithUiFont(new Border {", File.ReadAllText(Path.Combine(src, "MainWindow.Groups.cs")));
        Assert.Contains("CodePopup.WithUiFont(_menu);", File.ReadAllText(Path.Combine(src, "Infrastructure", "DarkMenu.cs")));
    }

    // Como na app: um botão de ícone (FontFamily = IconFont) numa janela e um Popup sem pai ancorado nele. A janela
    // abre fora do ecrã, sem ativar. Os recursos ficam no Popup (na app vêm da Application, que os testes não têm).
    private static Popup OpenOnIconButton(FrameworkElement child)
    {
        var button = new Button { Content = "", FontFamily = IconFont, Width = 34, Height = 34 };
        var window = new Window
        {
            Content = button, Width = 120, Height = 80, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            FontFamily = UiFont,
        };
        window.Show();
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = -4000,
            VerticalOffset = -4000,
            Child = child,
        };
        popup.Resources["UiFont"] = UiFont;
        popup.Resources["IconFont"] = IconFont;
        popup.IsOpen = true;
        popup.Closed += (_, _) => window.Close();
        return popup;
    }

    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Texts(child)) yield return found;
    }

    private static T OnSta<T>(Func<T> work, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
        => StaRunner.Run(work, nameof(CodePopupFontTests) + "." + test);

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
