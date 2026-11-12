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

    [Theory]
    [InlineData("data:text/html,https://accounts.google.com/signin", "data:text/html…")]
    [InlineData("data:,x", "data:…")]
    [InlineData("javascript:alert(1)", "javascript:…")]
    [InlineData("data:text/html;base64,PGgxPng8L2gxPg==", "data:text/html…")]
    [InlineData("DATA:text/html,x", "data:text/html…")]
    public void Split_DataUrl_HidesContent(string url, string rest)
        => Assert.Equal(new DisplayUrl(string.Empty, rest), UrlDisplay.Split(url));

    [Fact]
    public void Split_DataUrl_WithAnAbsurdType_ShowsNoType()
        => Assert.Equal(new DisplayUrl(string.Empty, "data:…"), UrlDisplay.Split("data:" + new string('a', 60) + ",x"));
}
