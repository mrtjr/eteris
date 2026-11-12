using System.IO;
using System.Windows.Media;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class SiteTintTests
{
    // MOT-02: o texto do theme-color vem da página e nunca pode chegar ao ColorConverter do WPF, que com
    // «ContextColor <endereço>» abre o endereço no processo principal. Os valores são um urn: e um ficheiro que não
    // existe, para que nem a fase vermelha faça um pedido de rede.
    [Theory]
    [InlineData("ContextColor urn:eteris:teste 1")]
    [InlineData("contextcolor file:///C:/nao-existe.icc 1")]
    [InlineData("sc#1 1 0 0")]
    [InlineData("sc#1,1,0,0")]
    [InlineData("#ff66")]
    [InlineData("rgba(1,2,3,1.2.3)")]
    [InlineData("rgb(\u0661,\u0662,\u0663)")]
    [InlineData("red ; x")]
    public void Parse_OnlyAcceptsPlainColours(string css) => Assert.Null(SiteTint.Parse(css));

    // O conteúdo do theme-color não tem limite de tamanho e o Parse corre na thread da janela: um «rgba(» com dezenas de
    // milhares de dígitos sem fecho não pode congelar o Éteris (a expressão do alfa retrocedia em tempo quadrático).
    [Fact]
    public void Parse_HugeInput_ReturnsAtOnce()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(SiteTint.Parse("rgba(1,2,3," + new string('1', 30_000)));
        Assert.Null(SiteTint.Parse("#" + new string('a', 30_000)));
        Assert.True(clock.ElapsedMilliseconds < 250, $"demorou {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Parse_EightDigitHex_IsCssOrder_AlphaLast()
    {
        Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), SiteTint.Parse("#112233ff"));
        Assert.Null(SiteTint.Parse("#11223300"));
    }

    [Fact]
    public void Parse_NeverHandsPageTextToTheWpfConverter()
    {
        var source = File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Theme", "SiteTint.cs"));
        Assert.DoesNotContain("ColorConverter", source);
        Assert.DoesNotContain("catch (FormatException)", source);
    }

    [Theory]
    [InlineData("#ff6600", 0xFF, 0x66, 0x00)]
    [InlineData("#F60", 0xFF, 0x66, 0x00)]
    [InlineData("  #1e2327  ", 0x1E, 0x23, 0x27)]
    [InlineData("rgb(30, 35, 39)", 30, 35, 39)]
    [InlineData("rgba(15,15,15,1)", 15, 15, 15)]
    [InlineData("red", 0xFF, 0x00, 0x00)]
    public void Parse_UnderstandsCssColors(string css, byte r, byte g, byte b)
        => Assert.Equal(Color.FromRgb(r, g, b), SiteTint.Parse(css));

    [Theory]
    [InlineData("")]
    [InlineData("rgba(0, 0, 0, 0)")]
    [InlineData("transparent")]
    [InlineData("not-a-color")]
    [InlineData("var(--x)")]
    public void Parse_TransparentOrInvalid_IsNull(string css)
        => Assert.Null(SiteTint.Parse(css));

    [Fact]
    public void ForChrome_LightSiteColor_KeepsDefaultChrome()
        => Assert.Null(SiteTint.ForChrome(Colors.White));

    [Fact]
    public void ForChrome_VividColor_StaysDarkEnoughForLightText()
    {
        var tint = SiteTint.ForChrome(Color.FromRgb(0xFF, 0x66, 0x00));
        Assert.NotNull(tint);
        Assert.True(SiteTint.Luminance(tint.Value) <= SiteTint.MaxChromeLuminance);
    }

    [Fact]
    public void ForChrome_KeepsTheSiteHue()
    {
        var tint = SiteTint.ForChrome(Color.FromRgb(0xFF, 0x66, 0x00))!.Value;
        Assert.True(tint.R > tint.B);
    }

    [Fact]
    public void ForChrome_DarkSiteColor_IsAdoptedExactly()
    {
        var steamHeader = Color.FromRgb(0x17, 0x1A, 0x21);
        Assert.Equal(steamHeader, SiteTint.ForChrome(steamHeader));
    }
}
