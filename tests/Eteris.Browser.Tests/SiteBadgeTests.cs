using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class SiteBadgeTests
{
    [Theory]
    [InlineData("https://github.com/anthropics", "G")]
    [InlineData("https://www.wikipedia.org", "W")]
    [InlineData("http://news.ycombinator.com/", "N")]
    [InlineData("https://éteris.pt", "É")]
    public void Letter_IsFirstLetterOfHostWithoutWww(string url, string expected)
        => Assert.Equal(expected, SiteBadge.Letter(url));

    [Theory]
    [InlineData("eteris://newtab")]
    [InlineData("about:blank")]
    [InlineData("não é url")]
    public void Letter_ForNonWebAddress_IsNull(string url)
        => Assert.Null(SiteBadge.Letter(url));

    [Fact]
    public void ColorIndex_IsStableAcrossCallsAndSubdomainsOfWww()
        => Assert.Equal(SiteBadge.ColorIndex("https://github.com/a"), SiteBadge.ColorIndex("https://www.github.com/b"));

    [Fact]
    public void ColorIndex_IsWithinPalette()
    {
        foreach (var host in new[] { "a.pt", "github.com", "wikipedia.org", "mozilla.org", "x.y.z.com" })
            Assert.InRange(SiteBadge.ColorIndex("https://" + host), 0, SiteBadge.PaletteSize - 1);
    }

    [Fact]
    public void ColorIndex_DiffersForDifferentSites()
        => Assert.NotEqual(SiteBadge.ColorIndex("https://github.com"), SiteBadge.ColorIndex("https://wikipedia.org"));
}
