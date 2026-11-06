using Eteris.Browser.Tabs;
using Microsoft.Web.WebView2.Core;

namespace Eteris.Browser.Tests;

public class VideoEngineTests
{
    [Theory]
    [InlineData("\u0001eteris-media format", true)]
    [InlineData("\u0001eteris-media patented-only", true)]
    [InlineData("\u0001eteris-media outra", false)]
    [InlineData("eteris-media format", false)]
    [InlineData(null, false)]
    public void IsReport_OnlyKnownReasons(string? message, bool expected) => Assert.Equal(expected, VideoEngine.IsReport(message));

    [Theory]
    [InlineData(CoreWebView2WebResourceContext.Script, "script")]
    [InlineData(CoreWebView2WebResourceContext.Image, "image")]
    [InlineData(CoreWebView2WebResourceContext.Fetch, "xmlhttprequest")]
    [InlineData(CoreWebView2WebResourceContext.Document, "document")]
    [InlineData(CoreWebView2WebResourceContext.Manifest, "other")]
    public void ToAdblock_UsesFilterListNames(CoreWebView2WebResourceContext context, string expected)
        => Assert.Equal(expected, VideoEngine.ToAdblock(context));

    [Fact]
    public void EdgeEngine_StartsWithoutSmartScreenOrBackgroundTraffic()
    {
        Assert.Contains("msSmartScreenProtection", VideoEngine.BrowserArguments);
        Assert.Contains("--disable-background-networking", VideoEngine.BrowserArguments);
    }

    [Theory]
    [InlineData("https://www.twitch.tv/", true)]
    [InlineData("http://a.pt/", true)]
    [InlineData("eteris://newtab", false)]
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void OnlyWebAddressesGoToTheVideoEngine(string? url, bool expected) => Assert.Equal(expected, VideoEngine.CanHost(url));
}
