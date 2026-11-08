using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// B10 — auto-teste de privacidade: o veredicto de cada linha, decidido com o que a página viu e o que o «servidor»
// do Éteris recebeu; tudo servido localmente (nomes .invalid) e medido pelo DevTools.
public class SelfTestTests
{
    private static Dictionary<string, string> H(params (string Name, string Value)[] headers)
        => headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Main = H(("Sec-GPC", "1"), ("DNT", "1"), ("Accept-Language", "pt-PT"));
    private static readonly Dictionary<string, string> FirstParty = H(("Cookie", "eteris_teste=abc"));
    private static readonly Dictionary<string, string> ThirdPartyClean = H(("Accept", "*/*"));
    private static readonly Dictionary<string, string> ReferrerOrigin = H(("Referer", SelfTest.PageUrl));

    private const string Policy = "disable_non_proxied_udp";

    private static SelfTest.Measured Good(string? canvasBefore = "abc", bool rtcApi = true) =>
        new(true, ["pt-PT"], true, false, rtcApi, "abc", canvasBefore, "r1");

    private static SelfTest.Check Find(IReadOnlyList<SelfTest.Check> checks, string id) => Assert.Single(checks, c => c.Id == id);

    [Fact]
    public void AllProtected_GivesEightLines()
    {
        var checks = SelfTest.Grade(Good(canvasBefore: "def"), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, lanBlocked: true, Policy);
        Assert.Equal(["gpc", "cookies", "referrer", "language", "webrtc", "lan", "ads", "canvas"], checks.Select(c => c.Id));
        Assert.All(checks, c => Assert.True(c.Passed, c.Id));
        Assert.Contains("«https://autoteste-eteris.invalid/»", Find(checks, "referrer").Detail);
        Assert.Contains("«disable_non_proxied_udp»", Find(checks, "webrtc").Detail);
    }

    [Fact]
    public void Gpc_NeedsBothTheScriptAndTheHeader()
    {
        Assert.False(Find(SelfTest.Grade(Good() with { Gpc = false }, Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, Policy), "gpc").Passed);
        var noHeader = Find(SelfTest.Grade(Good(), H(("Accept-Language", "pt-PT")), FirstParty, ThirdPartyClean, ReferrerOrigin, true, Policy), "gpc");
        Assert.False(noHeader.Passed);
        Assert.Contains("Sec-GPC", noHeader.Detail);
    }

    [Fact]
    public void ThirdPartyCookie_SentBack_IsExposed_AndAFailedControlIsInconclusive()
    {
        var leaked = Find(SelfTest.Grade(Good(), Main, FirstParty, H(("Cookie", "eteris_teste=abc")), ReferrerOrigin, true, Policy), "cookies");
        Assert.False(leaked.Passed);
        Assert.Contains("segui-lo de site em site", leaked.Detail);
        var noControl = Find(SelfTest.Grade(Good(), Main, H(), ThirdPartyClean, ReferrerOrigin, true, Policy), "cookies");
        Assert.Null(noControl.Passed);
        Assert.StartsWith("Inconclusivo", noControl.Detail);
    }

    [Fact]
    public void Referrer_FullAddressIsExposed_NothingIsFine_MissingIsInconclusive()
    {
        Assert.False(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, H(("Referer", SelfTest.PageUrl + "?segredo=1")), true, Policy), "referrer").Passed);
        Assert.True(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, H(), true, Policy), "referrer").Passed);
        Assert.Null(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, null, true, Policy), "referrer").Passed);
    }

    [Fact]
    public void Language_ListWithWeightsIsExposed()
    {
        var many = H(("Sec-GPC", "1"), ("Accept-Language", "pt-PT,pt;q=0.9,en;q=0.8"));
        Assert.False(Find(SelfTest.Grade(Good(), many, FirstParty, ThirdPartyClean, ReferrerOrigin, true, Policy), "language").Passed);
        Assert.False(Find(SelfTest.Grade(Good() with { Languages = ["pt-PT", "en"] }, Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, Policy), "language").Passed);
    }

    // Revisão aos 50: o WebRTC é lido no motor (a política em vigor no perfil da aba), nunca com uma ligação de teste —
    // recolher candidatos abria UDP 5353 à escuta e o Windows pedia exceção na firewall.
    [Theory]
    [InlineData("disable_non_proxied_udp", true)]
    [InlineData("default_public_interface_only", false)]
    [InlineData("default", false)]
    [InlineData(null, null)]
    public void WebRtc_IsTheEffectivePolicy(string? policy, bool? passed)
        => Assert.Equal(passed, Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, policy), "webrtc").Passed);

    [Fact]
    public void WebRtc_Unavailable_IsProtected_AndExposedNamesThePolicy()
    {
        var none = Find(SelfTest.Grade(Good(rtcApi: false), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, null), "webrtc");
        Assert.True(none.Passed);
        Assert.Contains("não está disponível", none.Detail);
        var exposed = Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, "default"), "webrtc");
        Assert.False(exposed.Passed);
        Assert.Contains("«default»", exposed.Detail);
        Assert.StartsWith("Inconclusivo", Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, null), "webrtc").Detail);
    }

    // As medições nunca abrem portas: nenhum RTCPeerConnection construído, só a pergunta se existe.
    [Fact]
    public void MeasureScript_OpensNoConnections()
    {
        Assert.DoesNotContain("new RTCPeerConnection", SelfTest.MeasureScript);
        Assert.DoesNotContain("createOffer", SelfTest.MeasureScript);
        Assert.Contains("typeof RTCPeerConnection", SelfTest.MeasureScript);
        Assert.Contains("GetPreference(Shields.PrivacyPreferences.WebRtcPreference)", Read("src", "Eteris.Browser", "MainWindow.SelfTest.cs"));
    }

    [Fact]
    public void LocalNetwork_MustBeStoppedByEteris()
    {
        Assert.False(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, lanBlocked: false, Policy), "lan").Passed);
        Assert.False(Find(SelfTest.Grade(Good() with { LanReached = true }, Main, FirstParty, ThirdPartyClean, ReferrerOrigin, false, Policy), "lan").Passed);
    }

    [Fact]
    public void Canvas_FirstRunWaits_SameIsExposed_DifferentIsProtected()
    {
        var first = Find(SelfTest.Grade(Good(canvasBefore: null), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, Policy), "canvas");
        Assert.Null(first.Passed);
        Assert.Contains("Repetir o teste", first.Detail);
        var same = Find(SelfTest.Grade(Good(canvasBefore: "abc"), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true, Policy), "canvas");
        Assert.False(same.Passed);
        Assert.Contains("Igual entre recargas", same.Detail);
    }

    [Fact]
    public void Parse_ReadsWhatThePageMeasured()
    {
        var m = SelfTest.Parse("""{"run":"r9","gpc":true,"languages":["pt-PT"],"adApis":true,"lanReached":false,"rtc":true,"canvas":"ff","canvasBefore":null}""");
        Assert.NotNull(m);
        Assert.Equal("r9", m.Run);
        Assert.Null(m.CanvasBefore);
        Assert.True(m.RtcApi);
        Assert.False(SelfTest.Parse("""{"rtc":false,"run":"x","gpc":true,"languages":[],"adApis":true,"lanReached":false,"canvas":"1"}""")!.RtcApi);
        Assert.Null(SelfTest.Parse("não é JSON"));
        Assert.Null(SelfTest.Parse(42.0));
    }

    [Fact]
    public void TestNames_NeverExistOnTheInternet()
    {
        Assert.EndsWith(".invalid", SelfTest.Site);
        Assert.EndsWith(".invalid", SelfTest.ThirdParty);
        // Sites diferentes, para o iframe ser mesmo de terceiros.
        Assert.NotEqual(Data.SiteScope.OfHost(SelfTest.Site), Data.SiteScope.OfHost(SelfTest.ThirdParty));
        Assert.True(SelfTest.IsTestPage("https://autoteste-eteris.invalid/"));
        Assert.False(SelfTest.IsTestPage("https://autoteste-eteris.invalid/set"));
        Assert.False(SelfTest.IsTestPage("http://autoteste-eteris.invalid/"));
        Assert.False(SelfTest.IsTestPage("https://example.com/"));
        Assert.DoesNotContain("<script", SelfTest.PageHtml);
    }

    [Fact]
    public void Results_AreShownAsText_NeverAsHtml()
    {
        var script = SelfTest.RenderScript([new("gpc", "GPC", true, "<img src=x onerror=alert(1)>")]);
        Assert.DoesNotContain("innerHTML", script);
        Assert.Contains("textContent = c.detail", script);
        Assert.DoesNotContain("<img", script);
    }

    // Revisão aos 50: os nomes .invalid não respondem a qualquer página (um fetch bastava para reconhecer o Éteris).
    [Theory]
    [InlineData("https://jornal.pt/artigo", "https://autoteste-eteris.invalid/", false, false, false)]
    [InlineData("https://jornal.pt/artigo", "https://terceiro-eteris.invalid/eco?k=x&run=1", false, false, false)]
    [InlineData("https://jornal.pt/artigo", "https://autoteste-eteris.invalid/", true, false, false)]
    [InlineData(null, "https://autoteste-eteris.invalid/", true, false, false)]
    [InlineData("about:blank", "https://autoteste-eteris.invalid/", true, false, false)]
    [InlineData("eteris://privacy/teste", "https://autoteste-eteris.invalid/", true, false, true)]
    [InlineData("eteris://privacy/teste", "https://autoteste-eteris.invalid/eco?k=c1&run=1", false, false, false)]
    [InlineData("https://jornal.pt/artigo", "https://autoteste-eteris.invalid/", true, true, true)]
    [InlineData("https://jornal.pt/artigo", "https://autoteste-eteris.invalid/eco?k=c1&run=1", true, true, false)]
    [InlineData("https://autoteste-eteris.invalid/", "https://autoteste-eteris.invalid/eco?k=c1&run=1", false, false, true)]
    [InlineData("https://autoteste-eteris.invalid/", "https://terceiro-eteris.invalid/set?run=1", false, false, true)]
    [InlineData("https://autoteste-eteris.invalid/", "https://autoteste-eteris.invalid/", true, false, true)]
    [InlineData("http://autoteste-eteris.invalid/", "https://autoteste-eteris.invalid/eco", false, false, false)]
    [InlineData("https://terceiro-eteris.invalid/", "https://autoteste-eteris.invalid/eco", false, false, false)]
    [InlineData("https://autoteste-eteris.invalid/", "https://exemplo.pt/", false, false, false)]
    public void Serves_OnlyTheTestPageAndTheNavigationThatOpensIt(string? main, string request, bool mainFrame, bool explicitOrHistory, bool expected)
        => Assert.Equal(expected, SelfTest.Serves(main, request, mainFrame, explicitOrHistory));

    // Revisão aos 50: a 22.ª execução já não apaga a meio os cabeçalhos dela própria (falsos ✘ no GPC e na língua).
    [Fact]
    public void Received_KeepsTheRunningRunAndTheMainRequest_DropsOnlyOldRuns()
    {
        SelfTest.Record("main", H(("Sec-GPC", "1")));
        for (var i = 0; i < 40; i++)
        {
            var run = "rev50-" + i;
            SelfTest.Record(run + "/c1", H(("Cookie", "eteris_teste=" + run)));
            SelfTest.Record(run + "/c3", H());
            SelfTest.Record(run + "/ref", H());
            Assert.NotNull(SelfTest.Got(run + "/c1"));
            Assert.NotNull(SelfTest.Got(run + "/c3"));
            Assert.Equal("1", SelfTest.Got("main")?["Sec-GPC"]);
        }
        Assert.Null(SelfTest.Got("rev50-0/c1"));
        Assert.NotNull(SelfTest.Got("rev50-39/ref"));
        Assert.NotNull(SelfTest.Got($"rev50-{40 - SelfTest.KeepRuns}/c1"));
        Assert.Null(SelfTest.Got($"rev50-{39 - SelfTest.KeepRuns}/c1"));
    }

    [Fact]
    public void Factory_FailsLikeAMissingName_ForAnyOtherPage()
    {
        var factory = Read("src", "Eteris.Browser", "Browser", "SelfTestSchemeHandlerFactory.cs");
        Assert.Contains("SelfTest.Serves(main, request.Url, mainFrame, explicitOrHistory)", factory);
        Assert.Contains("response.ErrorCode = CefErrorCode.NameNotResolved;", factory);
        Assert.DoesNotContain("Received.Clear()", Read("src", "Eteris.Browser", "Browser", "SelfTest.cs"));
    }

    // As peças estão ligadas: o «servidor» nos dois contextos, a rota eteris://privacy/teste, as medições pelo
    // DevTools ao fim do carregamento, fora do histórico e sem o cartão da rede local.
    [Fact]
    public void Wiring()
    {
        Assert.Contains("SelfTestSchemeHandlerFactory.Register(Cef.GetGlobalRequestContext());", Read("src", "Eteris.Browser", "App.xaml.cs"));
        Assert.Contains("SelfTestSchemeHandlerFactory.Register(context);", Read("src", "Eteris.Browser", "MainWindow.Private.cs"));
        Assert.Contains("AbsolutePath: \"/teste\"", Read("src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        var main = Read("src", "Eteris.Browser", "MainWindow.xaml.cs");
        Assert.Contains("if (SelfTest.IsTestPage(url)) _ = RunSelfTestAsync(tab, browser, url);", main);
        Assert.Contains("if (!IsPrivate && !SelfTest.IsTestPage(url))", main);
        Assert.Contains("PageScript.EvaluateAsync(browser, SelfTest.MeasureScript", Read("src", "Eteris.Browser", "MainWindow.SelfTest.cs"));
        Assert.Contains("SelfTest.IsTestHost(", Read("src", "Eteris.Browser", "MainWindow.LocalNetwork.cs"));
        Assert.Contains("href=\"eteris://privacy/teste\"", Read("src", "Eteris.Browser", "Browser", "privacy.html"));
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
