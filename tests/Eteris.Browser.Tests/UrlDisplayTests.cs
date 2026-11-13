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

    [Theory]
    [InlineData("https://accounts.google.com.conta-verificar.example/login", "accounts.google.com.", "conta-verificar.example", "/login")]
    [InlineData("https://www.github.com/x", "", "github.com", "/x")]
    [InlineData("https://pt.wikipedia.org/", "pt.", "wikipedia.org", "")]
    [InlineData("http://localhost:3000/x", "", "localhost:3000", "/x")]
    [InlineData("http://192.168.1.1/", "", "192.168.1.1", "")]
    [InlineData("https://a.b.exemplo.pt:8443/", "a.b.", "exemplo.pt:8443", "")]
    [InlineData("file:///C:/temp/a.html", "", "file:///C:/temp/a.html", "")]
    [InlineData("http://[::1]:8080/x", "", "[::1]:8080", "/x")]
    [InlineData("data:text/html,https://accounts.google.com/signin", "", "", "data:text/html…")]
    public void Parts_SeparatesRegistrableDomain(string url, string sub, string domain, string rest)
        => Assert.Equal(new DisplayParts(sub, domain, rest), UrlDisplay.Parts(url));

    [Fact]
    public void Fit_LongSubdomain_KeepsRegistrableDomainVisible()
    {
        var parts = UrlDisplay.Parts("https://accounts.google.com.conta-verificar.example/login");
        var fitted = UrlDisplay.Fit(parts, 30, text => text.Length);
        Assert.Equal(new DisplayParts("…com.", "conta-verificar.example", "/login"), fitted);
        Assert.Equal(parts, UrlDisplay.Fit(parts, 80, text => text.Length));
        // Nem o domínio cabe: corta-se pela esquerda, fica o fim (o sufixo) e o caminho sai.
        Assert.Equal(new DisplayParts("…", "verificar.example", ""), UrlDisplay.Fit(parts, 18, text => text.Length));
    }

    [Fact]
    public void Fit_DropsEveryLabel_ThenKeepsTheWholeDomainWhenItJustFits()
    {
        var parts = UrlDisplay.Parts("https://accounts.google.com.conta-verificar.example/login");
        Assert.Equal(new DisplayParts("…", "conta-verificar.example", "/login"), UrlDisplay.Fit(parts, 24, text => text.Length));
        Assert.Equal(new DisplayParts("…", "conta-verificar.example", "/login"), UrlDisplay.Fit(parts, 27, text => text.Length));
    }

    [Fact]
    public void Fit_WithoutSubdomains_OnlyCutsTheDomainFromTheLeft()
    {
        var parts = UrlDisplay.Parts("https://conta-verificar.example/login");
        Assert.Equal(parts, UrlDisplay.Fit(parts, 23, text => text.Length));
        Assert.Equal(new DisplayParts("…", "ar.example", ""), UrlDisplay.Fit(parts, 11, text => text.Length));
    }
}
