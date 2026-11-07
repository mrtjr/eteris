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

    private static bool AllowAfterClick(string clickedLink, string target, bool popupWindow = false, bool shieldOn = true,
        double secondsAgo = 0.1, bool interactive = false)
        => PopupPolicy.Allow(true, target, Opener, Engine, _ => shieldOn,
            new UserActivation(clickedLink, Now.AddSeconds(-secondsAgo), interactive), popupWindow, Now);

    [Fact]
    public void ClickOnThePage_ThatOpensAnotherSiteInATab_IsBlocked()
        => Assert.False(AllowAfterClick("", "https://whitebit.com/"));

    [Fact]
    public void ClickOnAChapterLink_ThatOpensAnAdInsteadOfTheLink_IsBlocked()
        => Assert.False(AllowAfterClick("https://imperiodabritannia.net/capitulo-2", "https://whitebit.com/", interactive: true));

    [Fact]
    public void ClickOnALinkToAnotherSite_OpensIt()
        => Assert.True(AllowAfterClick("https://discord.gg/abc", "https://discord.gg/abc", interactive: true));

    [Fact]
    public void ClickOnALink_OpensOtherPagesOfThatSite()
        => Assert.True(AllowAfterClick("https://www.example.com/a", "https://example.com/b?ref=1", interactive: true));

    [Fact]
    public void NewTabOnTheSameSite_Opens()
        => Assert.True(AllowAfterClick("", "https://imperiodabritannia.net/vip"));

    [Fact]
    public void LoginOrShareWindow_FromAButton_Opens()
        => Assert.True(AllowAfterClick("", "https://accounts.google.com/o/oauth2/auth", popupWindow: true, interactive: true));

    // PropellerAds no imperiodabritannia.net: clique na imagem do capítulo (tn=IMG) abria o casino numa aba.
    [Fact]
    public void PopupWindow_FromAClickOnAnImage_IsBlocked()
        => Assert.False(AllowAfterClick("", "https://uk.yieldergoverns.cfd/cx/abc", popupWindow: true));

    // O pedido da aba pode chegar ao motor antes do aviso do clique.
    [Fact]
    public void ClickReportedJustAfterThePopupRequest_StillCounts()
        => Assert.False(AllowAfterClick("", "https://uk.yieldergoverns.cfd/cx/abc", secondsAgo: -0.2));

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
        Assert.True(PopupPolicy.IsActivationReport(PopupPolicy.ActivationPrefix + "ihttps://a.pt/", out var link, out var interactive));
        Assert.Equal("https://a.pt/", link);
        Assert.True(interactive);
        Assert.True(PopupPolicy.IsActivationReport(PopupPolicy.ActivationPrefix + "-", out var none, out var content));
        Assert.Equal("", none);
        Assert.False(content);
        Assert.False(PopupPolicy.IsActivationReport("eteris-click x", out _, out _));
        Assert.False(PopupPolicy.IsActivationReport(PopupPolicy.ActivationPrefix, out _, out _));
        // Sem o segredo do processo (o formato antigo, que qualquer página escrevia) não é um clique.
        Assert.False(PopupPolicy.IsActivationReport("\u0001eteris-click ihttps://a.pt/", out _, out _));
    }
}
