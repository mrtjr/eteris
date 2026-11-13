using System.IO;
using System.Windows.Media;
using System.Xml.Linq;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

// O ponto «fora de casa» (7 px, no canto do ícone da aba) vê-se com ≥ 3:1 (WCAG 1.4.11) contra os fundos de uma aba, nos
// dois temas e com todas as cores do Éteris: a moldura (FrameBrush), a aba selecionada (SelectedBrush: lateral, modo
// compacto, multi-seleção) e a superfície da aba ativa no topo (ChromeSurfaceBrush, que o ApplyTint pinta com
// #F6F7FB no claro e, no escuro, com SiteTint.Base ou a cor do site até SiteTint.MaxChromeLuminance; com Espaços, com
// uma pitada da cor do Espaço).
// Os degraus do IrisBrush não chegam (o violeta sobre a barra escura de um site fica abaixo de 2:1; o ciano sobre o
// claro também): o ponto usa o HomeDotBrush, o 1.º degrau clareado ou escurecido até ter 3:1 contra todos esses fundos.
public class PinnedHomeContrastTests
{
    public static TheoryData<string, bool> Accents()
    {
        var data = new TheoryData<string, bool>();
        foreach (var choice in Accent.Palette)
        {
            data.Add(choice.Key, false);
            data.Add(choice.Key, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Accents))]
    public void HomeDot_StopsHaveContrastOnTabBackgrounds(string accent, bool light)
    {
        var stop = Accent.Gradient(Accent.PaletteColor(accent)!.Value, light)[0];
        var dot = Accent.HomeDot(stop, light);
        foreach (var background in Backgrounds(light))
            Assert.True(Accent.Contrast(dot, background) >= Accent.MinContrast,
                $"{accent} ({(light ? "claro" : "escuro")}): {dot} contra {background} = {Accent.Contrast(dot, background):0.00}");
        // Continua a ser a cor do Éteris (o mesmo matiz), só mais clara ou mais escura.
        var turn = Math.Abs(Hue(stop) - Hue(dot)) % 360;
        Assert.True(Math.Min(turn, 360 - turn) <= 4, $"{accent}: matiz {Hue(stop):0} → {Hue(dot):0}");
    }

    // Uma cor que já se vê fica como está.
    [Fact]
    public void HomeDot_KeepsAColorThatAlreadyShows()
        => Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), Accent.HomeDot(Color.FromRgb(0xFF, 0xFF, 0xFF), light: false));

    // O pincel acompanha a cor e o tema (as janelas privadas, sempre escuras, têm o seu); o ponto lê-o nos dois modelos.
    [Fact]
    public void HomeDotBrush_IsAppliedWithTheAccent()
    {
        var manager = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Theme", "AccentManager.cs"));
        Assert.Contains("resources[\"HomeDotBrush\"] = Frozen(new SolidColorBrush(dot));", manager);
        Assert.Contains("<SolidColorBrush x:Key=\"HomeDotBrush\"", File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Theme", "Theme.xaml")));
        Assert.Contains("Resources[\"HomeDotBrush\"] = new SolidColorBrush(Accent.HomeDot(Accent.Gradient(AccentManager.Current)[0], light: false));",
            File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "MainWindow.Theme.cs")));
        var xaml = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "MainWindow.xaml"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xaml, @"x:Name=""HomeDot"" [^>]*Fill=""\{DynamicResource HomeDotBrush\}""").Count);
    }

    // Antes do 1.º AccentManager.Apply, o pincel do tema já é o do íris no escuro (o tema de omissão).
    [Fact]
    public void HomeDotBrush_StartsAsTheIrisDot()
    {
        var theme = XDocument.Parse(File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Theme", "Theme.xaml")));
        var brush = theme.Descendants().Single(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "HomeDotBrush");
        Assert.Equal(Accent.HomeDot(Accent.IrisStops[0], light: false), (Color)ColorConverter.ConvertFromString((string)brush.Attribute("Color")!));
    }

    private static double Hue(Color c) => System.Drawing.Color.FromArgb(c.R, c.G, c.B).GetHue();

    private static IEnumerable<Color> Backgrounds(bool light)
    {
        var palette = XDocument.Parse(File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Theme", light ? "Light.xaml" : "Dark.xaml")));
        Color Brush(string key) => (Color)ColorConverter.ConvertFromString((string)palette.Descendants()
            .Single(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == key).Attribute("Color")!);
        yield return Brush("FrameBrush");
        yield return Brush("SelectedBrush");
        Color[] chromes = light ? [Color.FromRgb(0xF6, 0xF7, 0xFB)] : [SiteTint.Base, BrightestDarkChrome()];
        foreach (var chrome in chromes)
        {
            yield return chrome;
            // Com Espaços, a barra ganha uma pitada da cor do Espaço à vista (SiteTint.WithWorkspace).
            foreach (var space in Eteris.Browser.Tabs.TabGroup.Palette)
                yield return SiteTint.WithWorkspace(chrome, (Color)ColorConverter.ConvertFromString(space), light);
        }
    }

    // O cinzento mais claro que a barra pode ter no tema escuro (a cor de um site, limitada a SiteTint.MaxChromeLuminance).
    private static Color BrightestDarkChrome()
    {
        var level = Enumerable.Range(0, 256).Last(v => SiteTint.Luminance(Color.FromRgb((byte)v, (byte)v, (byte)v)) <= SiteTint.MaxChromeLuminance);
        return Color.FromRgb((byte)level, (byte)level, (byte)level);
    }

    [Fact]
    public void TheBackgrounds_AreTheOnesTheWindowPaints()
    {
        Assert.Contains("Color.FromRgb(0xF6, 0xF7, 0xFB)", File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "MainWindow.Theme.cs")));
        Assert.True(SiteTint.Luminance(BrightestDarkChrome()) > 0.06);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
