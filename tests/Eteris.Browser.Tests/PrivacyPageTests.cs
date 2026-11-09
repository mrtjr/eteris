using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B4 «Promessas verificáveis»: eteris://privacy/ e «Esconder o Éteris do Windows Recall e de capturas».
public class PrivacyPageTests
{
    private static Dictionary<string, string> EngineArgs() => new(CefConfigurator.Build("p").CefCommandLineArgs);

    private static PrivacyPage.State Good(Dictionary<string, string>? args = null) => new(
        args ?? EngineArgs(), PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
        SecureDns: null, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);

    private static PrivacyPage.Promise Find(PrivacyPage.State state, string id) => Assert.Single(PrivacyPage.Promises(state), p => p.Id == id);

    [Fact]
    public void Promises_WithTheRealEngineConfiguration_AreKept()
    {
        var state = Good();
        foreach (var id in new[] { "gpc-header", "gpc-js", "ad-apis", "quiet", "no-google", "cookies", "webrtc" })
            Assert.True(Find(state, id).Kept, id);
    }

    // As promessas são lidas do estado, não escritas à mão: tirar um interruptor faz a linha ficar ✘ e dizer qual.
    [Fact]
    public void AdApis_MissingSwitch_IsBrokenAndNamed()
    {
        var args = EngineArgs();
        args["disable-features"] = string.Join(',', EngineHardening.Switch(args, "disable-features").Where(f => f != "BrowsingTopics"));
        var promise = Find(Good(args), "ad-apis");
        Assert.False(promise.Kept);
        Assert.Contains("BrowsingTopics", promise.Detail);
    }

    // Revisão aos 50: a política de IP do WebRTC fixada na linha de comandos e sem o mDNS do WebRTC (UDP 5353 à escuta,
    // aviso da firewall do Windows); faltar qualquer um deixa a linha ✘ a dizer o quê.
    [Fact]
    public void WebRtc_NeedsThePolicySwitchAndNoMdns()
    {
        var noSwitch = EngineArgs();
        noSwitch.Remove(EngineHardening.WebRtcPolicySwitch);
        Assert.False(Find(Good(noSwitch), "webrtc").Kept);
        Assert.Contains("desde o arranque", Find(Good(noSwitch), "webrtc").Detail);
        var mdns = EngineArgs();
        mdns["disable-features"] = string.Join(',', EngineHardening.Switch(mdns, "disable-features").Where(f => f != "WebRtcHideLocalIpsWithMdns"));
        Assert.False(Find(Good(mdns), "webrtc").Kept);
        Assert.Contains("firewall", Find(Good(mdns), "webrtc").Detail);
        Assert.False(Find(Good() with { WebRtcProtected = false }, "webrtc").Kept);
    }

    // «No arranque, nada sai para a Google»: lida da linha de comandos do motor; sem um dos interruptores fica ✘ e diz qual.
    [Theory]
    [InlineData("NetworkTimeServiceQuerying")]
    [InlineData("AimEnabled")]
    public void NoGoogle_MissingSwitch_IsBrokenAndNamed(string feature)
    {
        Assert.True(Find(Good(), "no-google").Kept);
        var args = EngineArgs();
        args["disable-features"] = string.Join(',', EngineHardening.Switch(args, "disable-features").Where(f => f != feature));
        var promise = Find(Good(args), "no-google");
        Assert.False(promise.Kept);
        Assert.Contains(feature, promise.Detail);
    }

    // Por omissão (sincronização desligada) a promessa é só a do motor; com a sincronização ligada (escolha do
    // utilizador) continua cumprida e diz a exceção: só os pedidos da sincronização, cifrados, para o Drive dele, e quando.
    [Fact]
    public void NoGoogle_WithSyncOn_StaysKept_AndStatesTheException()
    {
        var off = Find(Good(), "no-google");
        Assert.True(off.Kept);
        Assert.DoesNotContain("sincronização", off.Detail);
        var on = Find(Good() with { SyncOn = true }, "no-google");
        Assert.True(on.Kept);
        Assert.EndsWith(PrivacyPage.SyncException, on.Detail);
        Assert.Contains("30 s depois de abrir", on.Detail);
        Assert.Contains("cifrado", on.Detail);
    }

    [Fact]
    public void Quiet_WithoutBackgroundNetworkingSwitch_IsBroken()
    {
        var args = EngineArgs();
        args.Remove("disable-background-networking");
        Assert.False(Find(Good(args), "quiet").Kept);
    }

    [Fact]
    public void GpcHeader_ReadsTheHeadersTheHandlerSends()
    {
        Assert.True(Find(Good(), "gpc-header").Kept);
        Assert.False(Find(Good() with { Headers = [("DNT", "1")] }, "gpc-header").Kept);
        Assert.False(Find(Good() with { Headers = [("Sec-GPC", "1"), ("DNT", "1")] }, "gpc-header").Kept);
    }

    [Fact]
    public void LiveChecks_AreDeclaredForGpcAndAdApis()
    {
        Assert.Equal("gpc", Find(Good(), "gpc-js").Live);
        Assert.Equal("ads", Find(Good(), "ad-apis").Live);
    }

    [Fact]
    public void Cookies_Off_IsBrokenWithAWayToFixIt()
    {
        var promise = Find(Good() with { ThirdPartyCookiesBlocked = false }, "cookies");
        Assert.False(promise.Kept);
        Assert.Equal("eteris://settings/#privacidade", promise.Fix);
    }

    [Fact]
    public void Optional_DnsAndRecallOff_AreNeitherKeptNorBroken()
    {
        Assert.Null(Find(Good(), "dns").Kept);
        Assert.Null(Find(Good(), "recall").Kept);
        Assert.True(Find(Good() with { SecureDns = "Quad9" }, "dns").Kept);
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(2, 1, false)]
    [InlineData(0, 0, false)]
    public void Recall_On_ChecksEveryVisibleWindow(int visible, int hidden, bool kept)
        => Assert.Equal(kept, Find(Good() with { HideFromCapture = true, VisibleWindows = visible, HiddenWindows = hidden }, "recall").Kept);

    [Fact]
    public void Render_FillsTheTemplateWithThePromises()
    {
        var html = PrivacyPage.Render(Good());
        Assert.DoesNotContain("{{DATA}}", html);
        Assert.Contains("\"id\":\"ad-apis\"", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("navigator.globalPrivacyControl === true", html);
        Assert.Contains("'browsingTopics' in document", html);
    }

    [Fact]
    public void Page_OpensFromTheCommandLineLikeTheOtherInternalPages()
        => Assert.Equal(PrivacyPage.Url, StartupArgs.InitialUrl([PrivacyPage.Url]));

    // Desligado por omissão (também na janela privada): ligado, as capturas de ecrã do próprio utilizador ficam vazias.
    [Fact]
    public void HideFromCapture_IsOffByDefault()
    {
        Assert.False(new UserSettings().HideFromCapture);
        Assert.True(new UserSettings().With("hideFromCapture", "true")!.HideFromCapture);
        Assert.Null(new UserSettings().With("hideFromCapture", "talvez"));
    }

    [Theory]
    [InlineData(false, 26100, CaptureShield.WdaNone)]
    [InlineData(true, 26100, CaptureShield.WdaExcludeFromCapture)]
    [InlineData(true, 19041, CaptureShield.WdaExcludeFromCapture)]
    [InlineData(true, 18363, CaptureShield.WdaMonitor)]
    public void Affinity_ExcludesFromCaptureWhereWindowsSupportsIt(bool hide, int build, uint expected)
        => Assert.Equal(expected, CaptureShield.AffinityFor(hide, build));

    [Fact]
    public void CaptureTexts_WarnThatCapturesBecomeEmpty()
    {
        Assert.Contains("vazio", CaptureText.Toast(true));
        Assert.Contains("Recall", CaptureText.BadgeTip(false));
        Assert.Contains("voltar a mostrar", CaptureText.BadgeTip(true));
    }

    [Fact]
    public void SettingsPage_OffersTheToggleWithItsWarningAndLinksToThePromises()
    {
        var html = SettingsPage.Render(new UserSettings { HideFromCapture = true });
        Assert.Contains("data-key=\"hideFromCapture\"", html);
        Assert.Contains("também ficam vazias", html);
        Assert.Contains("\"hideFromCapture\":true", html);
        Assert.Contains("href=\"eteris://privacy/\"", html);
    }
}
