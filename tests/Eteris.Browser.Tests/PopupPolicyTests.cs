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
}
