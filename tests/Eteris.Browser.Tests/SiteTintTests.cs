using System.Windows.Media;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class SiteTintTests
{
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
