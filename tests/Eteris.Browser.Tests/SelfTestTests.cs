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

    private static SelfTest.Measured Good(string? canvasBefore = "abc", IReadOnlyList<string>? rtc = null) =>
        new(true, ["pt-PT"], true, false, rtc ?? ["candidate:1 1 udp 2122260223 4f1b2c3d-aaaa.local 54321 typ host"], "abc", canvasBefore, "r1");

    private static SelfTest.Check Find(IReadOnlyList<SelfTest.Check> checks, string id) => Assert.Single(checks, c => c.Id == id);

    [Fact]
    public void AllProtected_GivesEightLines()
    {
        var checks = SelfTest.Grade(Good(canvasBefore: "def"), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, lanBlocked: true);
        Assert.Equal(["gpc", "cookies", "referrer", "language", "webrtc", "lan", "ads", "canvas"], checks.Select(c => c.Id));
        Assert.All(checks, c => Assert.True(c.Passed, c.Id));
        Assert.Contains("«https://autoteste-eteris.invalid/»", Find(checks, "referrer").Detail);
        Assert.Contains("1 candidato(s), nenhum com o IP da sua rede", Find(checks, "webrtc").Detail);
    }

    [Fact]
    public void Gpc_NeedsBothTheScriptAndTheHeader()
    {
        Assert.False(Find(SelfTest.Grade(Good() with { Gpc = false }, Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true), "gpc").Passed);
        var noHeader = Find(SelfTest.Grade(Good(), H(("Accept-Language", "pt-PT")), FirstParty, ThirdPartyClean, ReferrerOrigin, true), "gpc");
        Assert.False(noHeader.Passed);
        Assert.Contains("Sec-GPC", noHeader.Detail);
    }

    [Fact]
    public void ThirdPartyCookie_SentBack_IsExposed_AndAFailedControlIsInconclusive()
    {
        var leaked = Find(SelfTest.Grade(Good(), Main, FirstParty, H(("Cookie", "eteris_teste=abc")), ReferrerOrigin, true), "cookies");
        Assert.False(leaked.Passed);
        Assert.Contains("segui-lo de site em site", leaked.Detail);
        var noControl = Find(SelfTest.Grade(Good(), Main, H(), ThirdPartyClean, ReferrerOrigin, true), "cookies");
        Assert.Null(noControl.Passed);
        Assert.StartsWith("Inconclusivo", noControl.Detail);
    }

    [Fact]
    public void Referrer_FullAddressIsExposed_NothingIsFine_MissingIsInconclusive()
    {
        Assert.False(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, H(("Referer", SelfTest.PageUrl + "?segredo=1")), true), "referrer").Passed);
        Assert.True(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, H(), true), "referrer").Passed);
        Assert.Null(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, null, true), "referrer").Passed);
    }

    [Fact]
    public void Language_ListWithWeightsIsExposed()
    {
        var many = H(("Sec-GPC", "1"), ("Accept-Language", "pt-PT,pt;q=0.9,en;q=0.8"));
        Assert.False(Find(SelfTest.Grade(Good(), many, FirstParty, ThirdPartyClean, ReferrerOrigin, true), "language").Passed);
        Assert.False(Find(SelfTest.Grade(Good() with { Languages = ["pt-PT", "en"] }, Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true), "language").Passed);
    }

    [Theory]
    [InlineData("candidate:1 1 udp 2122260223 192.168.1.23 54321 typ host", false)]
    [InlineData("candidate:1 1 udp 2122260223 10.0.0.5 54321 typ host", false)]
    [InlineData("candidate:1 1 udp 2122260223 172.20.1.5 54321 typ host", false)]
    [InlineData("candidate:1 1 udp 2122260223 fe80::1c2:3ff:fe4 54321 typ host", false)]
    [InlineData("candidate:1 1 udp 2122260223 172.32.1.5 54321 typ host", true)]
    [InlineData("candidate:1 1 udp 2122260223 4f1b2c3d-aaaa.local 54321 typ host", true)]
    public void WebRtc_PrivateAddressesAreExposed(string candidate, bool passed)
        => Assert.Equal(passed, Find(SelfTest.Grade(Good(rtc: [candidate]), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true), "webrtc").Passed);

    [Fact]
    public void LocalNetwork_MustBeStoppedByEteris()
    {
        Assert.False(Find(SelfTest.Grade(Good(), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, lanBlocked: false), "lan").Passed);
        Assert.False(Find(SelfTest.Grade(Good() with { LanReached = true }, Main, FirstParty, ThirdPartyClean, ReferrerOrigin, false), "lan").Passed);
    }

    [Fact]
    public void Canvas_FirstRunWaits_SameIsExposed_DifferentIsProtected()
    {
        var first = Find(SelfTest.Grade(Good(canvasBefore: null), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true), "canvas");
        Assert.Null(first.Passed);
        Assert.Contains("Repetir o teste", first.Detail);
        var same = Find(SelfTest.Grade(Good(canvasBefore: "abc"), Main, FirstParty, ThirdPartyClean, ReferrerOrigin, true), "canvas");
        Assert.False(same.Passed);
        Assert.Contains("Igual entre recargas", same.Detail);
    }

    [Fact]
    public void Parse_ReadsWhatThePageMeasured()
    {
        var m = SelfTest.Parse("""{"run":"r9","gpc":true,"languages":["pt-PT"],"adApis":true,"lanReached":false,"rtc":["a"],"canvas":"ff","canvasBefore":null}""");
        Assert.NotNull(m);
        Assert.Equal("r9", m.Run);
        Assert.Null(m.CanvasBefore);
        Assert.Equal(["a"], m.Rtc!);
        Assert.Null(SelfTest.Parse("""{"rtc":null,"run":"x","gpc":true,"languages":[],"adApis":true,"lanReached":false,"canvas":"1"}""")!.Rtc);
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
