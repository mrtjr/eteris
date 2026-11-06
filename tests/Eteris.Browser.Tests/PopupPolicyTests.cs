using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class PopupPolicyTests
{
    private static readonly AdblockEngine Engine = AdblockEngine.Create("||ay267.com^\n");
    private const string Opener = "https://imperiodabritannia.net/capitulo-1";

    private static bool Allow(bool gesture, string target, bool shieldOn = true)
        => PopupPolicy.Allow(gesture, target, Opener, Engine, _ => shieldOn);

    [Fact]
    public void PopupWithoutUserGesture_IsBlocked()
        => Assert.False(Allow(gesture: false, "https://example.com/"));

    [Fact]
    public void PopupWithoutUserGesture_IsBlockedEvenWithShieldOff()
        => Assert.False(Allow(gesture: false, "https://example.com/", shieldOn: false));

    [Fact]
    public void ClickedLinkToCleanSite_Opens()
        => Assert.True(Allow(gesture: true, "https://pt.wikipedia.org/wiki/Mangá"));

    [Fact]
    public void ClickedPopupToListedRedirector_IsBlocked()
        => Assert.False(Allow(gesture: true, "https://ay267.com/?rb=tnGb4Qp"));

    [Fact]
    public void ClickedPopupToListedRedirector_OpensWhenUserTurnedShieldOff()
        => Assert.True(Allow(gesture: true, "https://ay267.com/?rb=tnGb4Qp", shieldOn: false));

    // ---------- Pop-unders (o anúncio do whitebit.com no imperiodabritannia.net) ----------

    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static bool AllowAfterClick(string clickedLink, string target, bool popupWindow = false, bool shieldOn = true, double secondsAgo = 0.1)
        => PopupPolicy.Allow(true, target, Opener, Engine, _ => shieldOn,
            new UserActivation(clickedLink, Now.AddSeconds(-secondsAgo)), popupWindow, Now);

    [Fact]
    public void ClickOnThePage_ThatOpensAnotherSiteInATab_IsBlocked()
        => Assert.False(AllowAfterClick("", "https://whitebit.com/"));

    [Fact]
    public void ClickOnAChapterLink_ThatOpensAnAdInsteadOfTheLink_IsBlocked()
        => Assert.False(AllowAfterClick("https://imperiodabritannia.net/capitulo-2", "https://whitebit.com/"));

    [Fact]
    public void ClickOnALinkToAnotherSite_OpensIt()
        => Assert.True(AllowAfterClick("https://discord.gg/abc", "https://discord.gg/abc"));

    [Fact]
    public void ClickOnALink_OpensOtherPagesOfThatSite()
        => Assert.True(AllowAfterClick("https://www.example.com/a", "https://example.com/b?ref=1"));

    [Fact]
    public void NewTabOnTheSameSite_Opens()
        => Assert.True(AllowAfterClick("", "https://imperiodabritannia.net/vip"));

    [Fact]
    public void LoginOrShareWindowWithSize_Opens()
        => Assert.True(AllowAfterClick("", "https://accounts.google.com/o/oauth2/auth", popupWindow: true));

    [Fact]
    public void WithShieldOff_EverythingTheUserClicksOpens()
        => Assert.True(AllowAfterClick("", "https://whitebit.com/", shieldOn: false));

    [Fact]
    public void WithoutARecentClickRecord_FallsBackToTheLists()
    {
        Assert.True(AllowAfterClick("", "https://whitebit.com/", secondsAgo: 30));
        Assert.True(PopupPolicy.Allow(true, "https://whitebit.com/", Opener, Engine, _ => true, activation: null));
    }

    [Fact]
    public void ActivationReport_IsParsed()
    {
        Assert.True(PopupPolicy.IsActivationReport("\u0001eteris-click https://a.pt/", out var link));
        Assert.Equal("https://a.pt/", link);
        Assert.True(PopupPolicy.IsActivationReport("\u0001eteris-click ", out var none));
        Assert.Equal("", none);
        Assert.False(PopupPolicy.IsActivationReport("eteris-click x", out _));
    }
}
