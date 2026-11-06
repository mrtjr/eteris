using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class UrlCleanerTests
{
    [Theory]
    [InlineData("https://www.publico.pt/artigo?utm_source=facebook&utm_medium=social", "https://www.publico.pt/artigo")]
    [InlineData("https://a.pt/x?id=42&fbclid=IwAR0abc", "https://a.pt/x?id=42")]
    [InlineData("https://a.pt/?gclid=xyz&q=fado&msclkid=1", "https://a.pt/?q=fado")]
    [InlineData("https://a.pt/x?mc_cid=1&mc_eid=2#secao", "https://a.pt/x#secao")]
    [InlineData("https://www.instagram.com/p/abc/?igshid=MzRl", "https://www.instagram.com/p/abc/")]
    public void Clean_RemovesTrackingParameters(string dirty, string clean)
        => Assert.Equal(clean, UrlCleaner.Clean(dirty));

    [Theory]
    [InlineData("https://a.pt/x?id=42&page=2")]
    [InlineData("https://www.youtube.com/watch?v=abc&t=30")]
    [InlineData("https://duckduckgo.com/?q=utm_source")]
    [InlineData("eteris://history/")]
    [InlineData("not a url")]
    public void Clean_LeavesOtherUrlsAlone(string url)
        => Assert.Equal(url, UrlCleaner.Clean(url));

    [Fact]
    public void Clean_KeepsEncodedValues()
        => Assert.Equal("https://a.pt/?q=s%C3%A3o%20paulo", UrlCleaner.Clean("https://a.pt/?q=s%C3%A3o%20paulo&utm_campaign=x"));

    [Fact]
    public void Setting_IsOnByDefault()
        => Assert.True(new Eteris.Browser.Data.UserSettings().CleanUrls);
}
