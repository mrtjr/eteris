using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class TrackerBlockerTests
{
    private static readonly TrackerBlocker Blocker = new(["doubleclick.net", "google-analytics.com", "connect.facebook.net"]);

    [Theory]
    [InlineData("https://www.google-analytics.com/analytics.js")]
    [InlineData("https://stats.g.doubleclick.net/r/collect")]
    [InlineData("https://doubleclick.net/x")]
    [InlineData("https://connect.facebook.net/en_US/fbevents.js")]
    public void ShouldBlock_KnownTrackerOnOtherSite(string request)
        => Assert.True(Blocker.ShouldBlock(request, "https://www.publico.pt/artigo"));

    [Theory]
    [InlineData("https://notdoubleclick.net/x")]
    [InlineData("https://doubleclick.net.evil.pt/x")]
    [InlineData("https://www.facebook.net/x")]
    [InlineData("https://cdn.publico.pt/app.js")]
    public void ShouldBlock_DoesNotOverMatch(string request)
        => Assert.False(Blocker.ShouldBlock(request, "https://www.publico.pt/artigo"));

    [Fact]
    public void ShouldBlock_FirstPartyRequestsAreAllowed()
        => Assert.False(Blocker.ShouldBlock("https://ad.doubleclick.net/pixel", "https://www.doubleclick.net/"));

    [Fact]
    public void ShouldBlock_WithoutPage_StillBlocksTracker()
        => Assert.True(Blocker.ShouldBlock("https://www.google-analytics.com/collect", null));

    [Theory]
    [InlineData("eteris://newtab/")]
    [InlineData("data:text/plain,x")]
    [InlineData("not a url")]
    public void ShouldBlock_NonWebRequestsAreAllowed(string request)
        => Assert.False(Blocker.ShouldBlock(request, "https://www.publico.pt/"));

    [Theory]
    [InlineData("www.publico.pt", "publico.pt")]
    [InlineData("a.b.bbc.co.uk", "bbc.co.uk")]
    [InlineData("loja.exemplo.com.br", "exemplo.com.br")]
    [InlineData("localhost", "localhost")]
    [InlineData("github.com", "github.com")]
    public void RegistrableDomain_ApproximatesPublicSuffix(string host, string expected)
        => Assert.Equal(expected, TrackerBlocker.RegistrableDomain(host));

    [Fact]
    public void Default_LoadsEmbeddedListWithMajorTrackers()
    {
        Assert.True(TrackerBlocker.Default.Count > 50);
        Assert.True(TrackerBlocker.Default.ShouldBlock("https://securepubads.g.doubleclick.net/tag/js/gpt.js", "https://www.publico.pt/"));
        Assert.True(TrackerBlocker.Default.ShouldBlock("https://www.googletagmanager.com/gtm.js?id=X", "https://www.publico.pt/"));
    }
}
