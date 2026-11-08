using System.IO;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B6 «Permissão para a rede local» (2/2): o cartão, o pedido do próprio Chromium e a promessa em eteris://privacy/.
public class LocalNetworkPermissionTests
{
    // O pedido de permissão do Chromium (Local Network Access) chega como «Rede local»: antes não tinha tradução e
    // era recusado sem o utilizador saber.
    [Theory]
    [InlineData(PermissionRequestType.LocalNetworkAccess)]
    [InlineData(PermissionRequestType.LocalNetwork)]
    [InlineData(PermissionRequestType.LoopbackNetwork)]
    [InlineData(PermissionRequestType.LocalNetwork | PermissionRequestType.LoopbackNetwork)]
    public void ChromiumPrompt_BecomesTheLocalNetworkPermission(PermissionRequestType type)
        => Assert.Equal([SitePermission.LocalNetwork], SitePermissionHandler.FromPrompt(type));

    [Fact]
    public void ChromiumPrompt_OtherKindsAreUnchanged()
        => Assert.Equal([SitePermission.Camera, SitePermission.Microphone],
            SitePermissionHandler.FromPrompt(PermissionRequestType.CameraStream | PermissionRequestType.MicStream));

    [Fact]
    public void Build_TurnsOnChromiumLocalNetworkChecks()
        => Assert.Contains("LocalNetworkAccessChecks", EngineHardening.Switch(CefConfigurator.Build("p").CefCommandLineArgs, "enable-features"));

    [Fact]
    public void Card_SaysWhereThePageTriedToGoAndWhenToAllow()
    {
        var detail = LocalNetworkText.CardDetail("este computador · 127.0.0.1:6463");
        Assert.StartsWith("Tentou chegar a este computador · 127.0.0.1:6463.", detail);
        Assert.Contains("Só permita se confiar", detail);
    }

    // «Permitir sempre» fica nas permissões do site, como as outras (aparece no cadeado e em Definições › Sites).
    [Fact]
    public void AlwaysAllow_IsStoredAndDecidedLikeTheOtherPermissions()
    {
        var path = Path.Combine(Path.GetTempPath(), "eteris-lan-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new SitePermissionStore(path);
            Assert.Equal(PermissionDecision.Ask, PermissionRules.Decide(store, "discord.com", [SitePermission.LocalNetwork]));
            store.Set("discord.com", SitePermission.LocalNetwork, true);
            Assert.Equal(PermissionDecision.Allow, PermissionRules.Decide(store, "discord.com", [SitePermission.LocalNetwork]));
            Assert.Contains((SitePermission.LocalNetwork, true), store.ForSite("discord.com"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static PrivacyPage.State State(Dictionary<string, string> args) => new(
        args, PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
        SecureDns: null, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);

    [Fact]
    public void PrivacyPage_ShowsTheLocalNetworkPromise()
    {
        var args = new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs);
        Assert.True(Assert.Single(PrivacyPage.Promises(State(args)), p => p.Id == "lan").Kept);
        args["enable-features"] = string.Join(',', EngineHardening.Switch(args, "enable-features").Where(f => f != "LocalNetworkAccessChecks"));
        Assert.False(Assert.Single(PrivacyPage.Promises(State(args)), p => p.Id == "lan").Kept);
    }

    [Fact]
    public void WhatsNew_HighlightsThePromisesAndTheLocalNetwork()
    {
        Assert.Contains(WhatsNewPage.Highlights, h => h.Detail.Contains("eteris://privacy/", StringComparison.Ordinal) && h.Detail.Contains("promessa", StringComparison.Ordinal));
        Assert.Contains(WhatsNewPage.Highlights, h => h.Title == "A rede local é sua");
    }
}
