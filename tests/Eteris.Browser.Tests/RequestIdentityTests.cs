using System.Globalization;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B5 «Fundações de rede» (2/2): uma só língua e dicas do cliente mínimas.
public class RequestIdentityTests
{
    [Theory]
    [InlineData("pt-PT", "en-GB", "pt-PT")]
    [InlineData("en-US", "pt-BR", "en-US")]
    [InlineData("system", "pt-BR", "pt-BR")]
    [InlineData("system", "de-DE", "de-DE")]
    [InlineData("system", "pt", "pt-BR")]
    [InlineData("system", "", "pt-PT")]
    [InlineData("klingon", "pt-BR", "pt-PT")]
    public void Language_IsAlwaysASingleTagWithRegion(string choice, string system, string expected)
    {
        var language = RequestIdentity.Language(choice, system.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(system));
        Assert.Equal(expected, language);
        Assert.DoesNotContain(",", language);
        Assert.DoesNotContain(";q=", language);
    }

    [Fact]
    public void Setting_DefaultsToPortugueseAndOnlyAcceptsKnownChoices()
    {
        Assert.Equal("pt-PT", new UserSettings().SiteLanguage);
        Assert.Equal("system", new UserSettings().With("siteLanguage", "system")!.SiteLanguage);
        Assert.Null(new UserSettings().With("siteLanguage", "pt-PT,en;q=0.5"));
    }

    [Theory]
    [InlineData("Sec-CH-UA-Full-Version-List", true)]
    [InlineData("sec-ch-ua-platform-version", true)]
    [InlineData("Sec-CH-UA-Arch", true)]
    [InlineData("Sec-CH-UA-Bitness", true)]
    [InlineData("Sec-CH-UA-Model", true)]
    [InlineData("Sec-CH-UA-WoW64", true)]
    [InlineData("Sec-CH-Device-Memory", true)]
    [InlineData("Sec-CH-Viewport-Width", true)]
    [InlineData("Device-Memory", true)]
    [InlineData("DPR", true)]
    [InlineData("Sec-CH-UA", false)]
    [InlineData("sec-ch-ua-mobile", false)]
    [InlineData("Sec-CH-UA-Platform", false)]
    [InlineData("Accept-Language", false)]
    [InlineData("Sec-Fetch-Site", false)]
    [InlineData("Sec-GPC", false)]
    // Preferências que o CSS já mostra a qualquer página: o tema escuro do servidor continua a funcionar.
    [InlineData("Sec-CH-Prefers-Color-Scheme", false)]
    [InlineData("Sec-CH-Prefers-Reduced-Motion", false)]
    [InlineData("sec-ch-prefers-reduced-transparency", false)]
    [InlineData("Sec-CH-Prefers-Contrast", false)]
    public void Hints_OnlyTheLowEntropyOnesLeave(string header, bool stripped)
        => Assert.Equal(stripped, RequestIdentity.IsIdentifyingHint(header));

    // Revisão aos 50: as navegações levam sempre a língua do motor; num fetch em que a página escolheu a língua, a
    // dela fica — só a lista com pesos que o motor gera é reduzida.
    [Theory]
    [InlineData(null, true, "pt-PT")]
    [InlineData("en", true, "pt-PT")]
    [InlineData("pt-PT", true, null)]
    [InlineData(null, false, "pt-PT")]
    [InlineData("", false, "pt-PT")]
    [InlineData("en", false, null)]
    [InlineData("en-US,en;q=0.9", false, null)]
    [InlineData("pt-PT", false, null)]
    [InlineData("pt-PT,pt;q=0.9", false, "pt-PT")]
    public void AcceptLanguage_KeepsWhatThePageChose(string? current, bool navigation, string? expected)
        => Assert.Equal(expected, RequestIdentity.AcceptLanguage(current, "pt-PT", navigation));

    // A vista móvel de um site fixado continua a dizer «telemóvel Android» (dicas de baixa entropia).
    [Fact]
    public void MobileView_HeadersAreNotStripped()
        => Assert.All(PrivacyRequestHandler.MobileHeaders("UA").Where(h => h.Name != "User-Agent"),
            h => Assert.False(RequestIdentity.IsIdentifyingHint(h.Name)));

    [Fact]
    public void SettingsPage_OffersTheLanguageWithTheWindowsOne()
    {
        var html = SettingsPage.Render(new UserSettings { SiteLanguage = "en-US" });
        Assert.Contains("data-key=\"siteLanguage\"", html);
        Assert.Contains("\"siteLanguage\":\"en-US\"", html);
        Assert.Contains("\"systemLanguage\":\"", html);
    }

    private static PrivacyPage.State State(string language = "pt-PT", Dictionary<string, string>? args = null) => new(
        args ?? new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs), PrivacySignals.Headers, GpcScript: true,
        ThirdPartyCookiesBlocked: true, WebRtcProtected: true, SecureDns: null, Language: language,
        HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);

    [Fact]
    public void PrivacyPage_ShowsTheLanguageSentAndChecksItLive()
    {
        var row = Assert.Single(PrivacyPage.Promises(State("pt-BR")), p => p.Id == "language");
        Assert.True(row.Kept);
        Assert.Contains("«pt-BR»", row.Detail);
        Assert.Equal("language", row.Live);
        Assert.Contains("navigator.languages.length === 1", PrivacyPage.Render(State()));
    }

    // Sem a redução do Chromium, o cabeçalho levava também a língua-base com peso: a linha fica ✘.
    [Fact]
    public void PrivacyPage_WithoutTheReduction_IsBroken()
    {
        var args = new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs);
        args["enable-features"] = string.Join(',', EngineHardening.Switch(args, "enable-features").Where(f => f != "ReduceAcceptLanguage"));
        Assert.False(Assert.Single(PrivacyPage.Promises(State(args: args)), p => p.Id == "language").Kept);
        Assert.False(Assert.Single(PrivacyPage.Promises(State("pt-PT,en")), p => p.Id == "language").Kept);
    }

    [Theory]
    [InlineData("ReduceAcceptLanguage")]
    [InlineData("ReduceAcceptLanguageHTTP")]
    public void Build_ReducesAcceptLanguage(string feature)
        => Assert.Contains(feature, EngineHardening.Switch(CefConfigurator.Build("p").CefCommandLineArgs, "enable-features"));

    // A lista de línguas do motor é uma só, fixada ao arrancar.
    [Fact]
    public void Configure_SetsASingleAcceptLanguage()
    {
        var settings = new CefSharp.Wpf.HwndHost.CefSettings();
        EngineHardening.Configure(settings, "pt-BR");
        Assert.Equal("pt-BR", settings.AcceptLanguageList);
        Assert.Equal("pt-BR", EngineHardening.StartedWithLanguage);
        Assert.Contains("BrowsingTopics", EngineHardening.Switch(settings.CefCommandLineArgs, "disable-features"));
    }

    // Cookies de terceiros: com um tratador de pedidos é o CEF que os junta ao pedido, por isso o Éteris decide.
    [Theory]
    [InlineData("https://www.publico.pt/", "https://ads.tracker.com/p.gif", CefSharp.ResourceType.Image, true, false, false)]
    [InlineData("https://www.publico.pt/", "https://imagens.publico.pt/x.jpg", CefSharp.ResourceType.Image, true, false, true)]
    [InlineData("https://www.publico.pt/", "https://login.exemplo.com/", CefSharp.ResourceType.MainFrame, true, false, true)]
    [InlineData("http://127.0.0.1:8765/rede.html", "http://localhost:8766/echo", CefSharp.ResourceType.SubFrame, true, false, false)]
    [InlineData("http://127.0.0.1:8765/rede.html", "http://127.0.0.1:8766/echo", CefSharp.ResourceType.SubFrame, true, false, true)]
    [InlineData("https://www.publico.pt/", "https://ads.tracker.com/p.gif", CefSharp.ResourceType.Image, false, false, true)]
    [InlineData("https://www.publico.pt/", "https://ads.tracker.com/p.gif", CefSharp.ResourceType.Image, true, true, true)]
    [InlineData("eteris://newtab/", "https://www.publico.pt/favicon.ico", CefSharp.ResourceType.Image, true, false, true)]
    [InlineData(null, "https://www.publico.pt/", CefSharp.ResourceType.Script, true, false, true)]
    public void ThirdPartyCookies_OnlyTheTopSiteSendsAndSaves(string? top, string request, CefSharp.ResourceType type, bool block, bool shieldOff, bool allowed)
        => Assert.Equal(allowed, ThirdPartyCookies.Allow(top, request, type, block, shieldOff));

    [Fact]
    public void PrivacyPage_HintsRowUsesTheHandlerDecision()
        => Assert.True(Assert.Single(PrivacyPage.Promises(State()), p => p.Id == "hints").Kept);
}
