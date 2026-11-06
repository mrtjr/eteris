using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class StartupPagesTests
{
    [Fact]
    public void FirstRun_ShowsWelcome()
        => Assert.Equal([new StartupTab(WelcomePage.Url, true)], StartupPages.Plan(true, "", "0.2.0", null));

    [Fact]
    public void FirstRunWithALink_OpensTheLinkInFrontAndWelcomeBehind_NeverWhatsNew()
    {
        var plan = StartupPages.Plan(true, "", "0.2.0", "https://a.pt/");
        Assert.Equal([new StartupTab("https://a.pt/", true), new StartupTab(WelcomePage.Url, false)], plan);
        Assert.DoesNotContain(plan, t => t.Url == WhatsNewPage.Url);
    }

    [Fact]
    public void AfterAnUpdate_ShowsWhatsNewOnce()
    {
        Assert.Equal([new StartupTab(WhatsNewPage.Url, true)], StartupPages.Plan(false, "0.1.0", "0.2.0", null));
        Assert.Empty(StartupPages.Plan(false, "0.2.0", "0.2.0", null));
    }
}
