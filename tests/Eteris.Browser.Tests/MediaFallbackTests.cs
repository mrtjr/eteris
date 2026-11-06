using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class MediaFallbackTests
{
    [Theory]
    [InlineData("\u0001eteris-media format", "format")]
    [InlineData("\u0001eteris-media patented-only", "patented-only")]
    [InlineData("\u0001eteris-media outra-coisa", null)]
    [InlineData("eteris-media format", null)]
    [InlineData(null, null)]
    public void ParseReport_AcceptsOnlyKnownReasons(string? message, string? expected)
        => Assert.Equal(expected, MediaFallback.ParseReport(message));

    [Theory]
    [InlineData("ChromeHTML", "https://www.twitch.tv/x", "https://www.twitch.tv/x")]
    [InlineData("FirefoxURL-308046B0AF4A39CB", "https://a.pt/", "https://a.pt/")]
    [InlineData("EterisHTML", "https://a.pt/", "microsoft-edge:https://a.pt/")]
    [InlineData(null, "https://a.pt/", "microsoft-edge:https://a.pt/")]
    public void SystemBrowserTarget_NeverSendsThePageBackToEteris(string? progId, string url, string expected)
        => Assert.Equal(expected, MediaFallback.SystemBrowserTarget(progId, url));

    [Fact]
    public void WatchScript_FlagsOnlyVideosThatNeverGetASource()
    {
        Assert.Contains("if (!rejected || attached) return;", MediaFallback.WatchScript);
        Assert.Contains("v.readyState === 0 && !v.currentSrc && !v.srcObject", MediaFallback.WatchScript);
        Assert.Contains("media.error.code === 4", MediaFallback.WatchScript);
    }
}
