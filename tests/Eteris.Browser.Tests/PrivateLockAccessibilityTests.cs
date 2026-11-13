using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace Eteris.Browser.Tests;

// O cartão da janela privada trancada (MainWindow.PrivateLock.cs) para o leitor de ecrã, o teclado, o alto contraste e os
// dois temas.
[Collection(WpfCollection.Name)]
public class PrivateLockAccessibilityTests
{
    // Um Grid não tem par de automação: sem este, o nome «Janela privada trancada» nunca chegava ao leitor de ecrã.
    [Fact]
    public void TheCard_IsANamedPane()
    {
        var (type, className) = StaRunner.Run(() =>
        {
            var card = new MainWindow.PrivateLockSurface();
            var peer = UIElementAutomationPeer.CreatePeerForElement(card);
            return (peer?.GetAutomationControlType(), peer?.GetClassName());
        }, nameof(PrivateLockAccessibilityTests) + "." + nameof(TheCard_IsANamedPane));
        Assert.Equal(AutomationControlType.Pane, type);
        Assert.Equal("PrivateLockSurface", className);
    }

    [Fact]
    public void TheCard_IsReadableAndHighContrastReady()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.PrivateLock.cs"));
        Assert.Contains("AutomationProperties.SetName(view, PrivateLockText.CardTitle);", code);
        Assert.Contains("AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);", code);
        Assert.Contains("SystemParameters.HighContrast", code);
        Assert.Contains("SystemColors.WindowBrush", code);
        Assert.Contains("SystemColors.WindowTextBrush", code);
        Assert.Contains("SystemColors.HighlightBrush", code);
        Assert.Contains("KeyboardNavigationMode.Cycle", code);
        Assert.Contains("AutomationProperties.SetAcceleratorKey(unlock, \"Enter\");", code);
        Assert.Contains("FocusableButtonStyle(\"PrimaryPillButton\"", code);
        // O cadeado é decorativo: não chega ao leitor de ecrã como um carácter estranho.
        Assert.Contains("new LockGlyph", code);
        // Só a janela ativa recebe o teclado; as outras guardam o botão como foco lógico.
        Assert.Matches(@"if \(IsActive\) Keyboard\.Focus\(unlock\);\s*else FocusManager\.SetFocusedElement\(this, unlock\);", code);
        // O cartão nunca mostra nada da página: nenhum título, endereço ou nome de site é lido para o construir.
        var build = Regex.Match(code, @"private PrivateLockSurface BuildPrivateLockView\(PrivateLockState state\)\s*\{(?<body>.*?)\r?\n    \}", RegexOptions.Singleline).Groups["body"].Value;
        Assert.NotEmpty(build);
        foreach (var leak in new[] { ".Title", ".Address", "Favicon", "WebHost(", "CapturingSites", "Tabs.Tabs" })
            Assert.DoesNotContain(leak, build);
    }

    // O texto e a nota sobre a superfície do tema: ≥ 4,5:1 nos dois temas; o cadeado (o texto do tema) ≥ 3:1.
    [Theory]
    [InlineData("Dark.xaml")]
    [InlineData("Light.xaml")]
    public void TheCard_HasContrastInBothThemes(string theme)
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Theme", theme));
        int Color(string key) => Convert.ToInt32(Regex.Match(xaml, "x:Key=\"" + key + "\" Color=\"#([0-9A-Fa-f]{6})\"").Groups[1].Value, 16);
        var surface = Color("SurfaceBrush");
        Assert.True(Contrast(Color("TextBrush"), surface) >= 4.5, theme + ": TextBrush");
        Assert.True(Contrast(Color("TextMutedBrush"), surface) >= 4.5, theme + ": TextMutedBrush");
    }

    private static double Contrast(int a, int b)
    {
        static double Channel(int c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(int rgb) => 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Eteris.Browser"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
