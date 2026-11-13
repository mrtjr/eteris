using System.Windows.Media;
using Eteris.Browser.Browser;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

// A Cor do Éteris chega às páginas internas (eteris://) e às que já estão abertas.
public class AccentPagesTests
{
    private const string Page = """<!doctype html><html lang="pt"><head><meta name="color-scheme" content="dark"></head><body></body></html>""";
    private static readonly Color[] Coral = Accent.Gradient(Accent.PaletteColor("coral")!.Value);

    [Fact]
    public void Themed_WithTheOriginalIris_InjectsOnlyTheSkin()
        => Assert.Equal(Page.Replace("<head>", "<head>" + EterisSkin.Style), InternalPages.Themed(Page, light: false, Accent.IrisStops));

    // Com a pele presente, a cor continua a ganhar: a pele, os estilos da própria página e depois o #eteris-accent.
    [Fact]
    public void Themed_OrdersSkinThenPageStylesThenAccent()
    {
        var page = Page.Replace("</head>", "<style id=\"own\">:root { --accent: #fff; }</style></head>");
        var html = InternalPages.Themed(page, light: true, Coral);
        var skin = html.IndexOf("id=\"eteris-skin\"", StringComparison.Ordinal);
        var own = html.IndexOf("id=\"own\"", StringComparison.Ordinal);
        var accent = html.IndexOf("id=\"eteris-accent\"", StringComparison.Ordinal);
        Assert.True(skin >= 0 && skin < own && own < accent, $"pele {skin}, página {own}, cor {accent}");
        Assert.True(accent < html.IndexOf("</head>", StringComparison.Ordinal));
    }

    [Fact]
    public void Themed_WithAnotherAccent_OverridesTheIrisVariable()
    {
        var html = InternalPages.Themed(Page, light: false, Coral);
        Assert.Contains("--iris: linear-gradient(90deg, " + Accent.Hex(Coral[0]), html);
        Assert.Contains("--accent: " + Accent.Hex(Coral[0]), html);
        Assert.True(html.IndexOf("</style>", StringComparison.Ordinal) < html.IndexOf("</head>", StringComparison.Ordinal));
    }

    [Fact]
    public void Themed_LightAndAccent_Combine()
    {
        var html = InternalPages.Themed(Page, light: true, Coral);
        Assert.Contains("class=\"light\"", html);
        Assert.Contains("--accent:", html);
    }

    // O script para as páginas já abertas só mexe nas variáveis CSS (sem HTML, sem aspas por fechar).
    [Fact]
    public void AccentScript_SetsTheVariables()
    {
        var script = InternalPages.AccentScript(Coral);
        Assert.Contains("setProperty('--iris'", script);
        Assert.Contains("setProperty('--accent'", script);
        Assert.DoesNotContain("<", script);
        Assert.Equal(0, script.Count(c => c == '"'));
    }

    [Fact]
    public void AccentScript_ForTheOriginalIris_RemovesTheOverride()
        => Assert.Contains("removeProperty('--iris')", InternalPages.AccentScript(Accent.IrisStops));
}

public class WallpaperAccentTests
{
    // As duas paisagens embutidas têm cor (não são cinzentas); «none» não tem paisagem.
    [Theory]
    [InlineData("coast")]
    [InlineData("mountains")]
    public void EmbeddedWallpapers_YieldAnAccentColor(string wallpaper)
    {
        var color = WallpaperAccent.Sample(wallpaper);
        Assert.NotNull(color);
        var (_, s, l) = Accent.ToHsl(color!.Value);
        Assert.InRange(s, 0.5, 0.85);
        Assert.InRange(l, 0.55, 0.65);
        // Lida uma vez: a segunda chamada devolve o mesmo.
        Assert.Equal(color, WallpaperAccent.Sample(wallpaper));
    }

    [Fact]
    public void NoWallpaper_HasNoColor()
    {
        Assert.Null(WallpaperAccent.Sample("none"));
        Assert.Null(WallpaperAccent.Sample("does-not-exist"));
    }
}
