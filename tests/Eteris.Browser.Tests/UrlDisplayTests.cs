using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class UrlDisplayTests
{
    [Theory]
    [InlineData("https://pt.wikipedia.org/wiki/Wikip%C3%A9dia:P%C3%A1gina_principal", "pt.wikipedia.org", "/wiki/Wikipédia:Página_principal")]
    [InlineData("https://github.com/", "github.com", "")]
    [InlineData("https://www.github.com/anthropics?tab=repos", "github.com", "/anthropics?tab=repos")]
    [InlineData("http://localhost:3000/x", "localhost:3000", "/x")]
    public void Split_HidesSchemeAndWwwAndDecodes(string url, string host, string rest)
    {
        var display = UrlDisplay.Split(url);
        Assert.Equal(host, display.Host);
        Assert.Equal(rest, display.Rest);
    }

    [Fact]
    public void Split_KeepsEncodedSpacesAndControlCharsEncoded()
        => Assert.Equal("/a%20b/%0A", UrlDisplay.Split("https://x.pt/a%20b/%0A").Rest);

    [Fact]
    public void Split_NonWebAddress_ShowsWholeAddressAsHost()
    {
        var display = UrlDisplay.Split("file:///C:/temp/a.html");
        Assert.Equal("file:///C:/temp/a.html", display.Host);
        Assert.Equal("", display.Rest);
    }

    [Theory]
    [InlineData("https://x.pt", true)]
    [InlineData("http://x.pt", false)]
    [InlineData("eteris://newtab", true)]
    public void IsSecure_ReflectsScheme(string url, bool secure)
        => Assert.Equal(secure, UrlDisplay.IsSecure(url));
}
