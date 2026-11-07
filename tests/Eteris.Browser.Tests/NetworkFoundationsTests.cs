using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B5 «Fundações de rede»: frascos por site de topo e referrer entre sites só com a origem.
public class NetworkFoundationsTests
{
    [Theory]
    // Para outro site: só a origem, mesmo com caminho, pesquisa e fragmento.
    [InlineData("https://www.publico.pt/artigo/123?utm=x#c", "https://ads.example.com/pixel.gif", "Origin", "https://www.publico.pt/")]
    [InlineData("http://127.0.0.1:8765/rede.html", "http://localhost:8766/echo", "Origin", "http://127.0.0.1:8765/")]
    [InlineData("http://exemplo.pt/a", "https://outro.pt/b", "Origin", "http://exemplo.pt/")]
    // O mesmo site (subdomínios, outra porta no mesmo IP, sufixos de dois níveis): nada muda.
    [InlineData("https://www.publico.pt/artigo/123", "https://imagens.publico.pt/x.jpg", "Keep", null)]
    [InlineData("http://127.0.0.1:8765/rede.html", "http://127.0.0.1:8766/echo", "Keep", null)]
    [InlineData("https://www.bbc.co.uk/news/1", "https://static.bbc.co.uk/s.js", "Keep", null)]
    // De https para http (outro site) não vai nada.
    [InlineData("https://www.publico.pt/artigo", "http://neverssl.com/", "Strip", null)]
    // Já é só a origem, não há referrer, ou não é a Web: fica como está.
    [InlineData("https://www.publico.pt/", "https://ads.example.com/", "Keep", null)]
    [InlineData(null, "https://ads.example.com/", "Keep", null)]
    [InlineData("", "https://ads.example.com/", "Keep", null)]
    [InlineData("eteris://newtab/", "https://www.publico.pt/", "Keep", null)]
    [InlineData("https://www.publico.pt/a", "data:text/plain,x", "Keep", null)]
    public void Referrer_CrossSiteKeepsOnlyTheOrigin(string? referrer, string request, string kind, string? value)
    {
        var decision = ReferrerCap.Decide(referrer, request);
        Assert.Equal(kind, decision.Kind.ToString());
        Assert.Equal(value, decision.Value);
    }

    // «Mesmo site» com esquema (como o Chromium): http e https do mesmo domínio são sites diferentes.
    [Fact]
    public void SameSite_IsSchemeful()
    {
        Assert.False(ReferrerCap.SameSite(new Uri("http://a.pt/"), new Uri("https://a.pt/")));
        Assert.True(ReferrerCap.SameSite(new Uri("https://www.a.pt/"), new Uri("https://b.a.pt/")));
    }

    [Theory]
    [InlineData("ThirdPartyStoragePartitioning")]
    [InlineData("PartitionedCookies")]
    [InlineData("SplitCacheByNetworkIsolationKey")]
    [InlineData("PartitionConnectionsByNetworkIsolationKey")]
    public void Build_PartitionsByTopLevelSite(string feature)
        => Assert.Contains(feature, EngineHardening.Switch(CefConfigurator.Build("p").CefCommandLineArgs, "enable-features"));

    [Fact]
    public void Apply_KeepsEarlierEnabledFeatures()
    {
        var args = new Dictionary<string, string> { ["enable-features"] = "Outra" };
        EngineHardening.Apply(args);
        var enabled = args["enable-features"].Split(',');
        Assert.Equal("Outra", enabled[0]);
        Assert.Contains("ThirdPartyStoragePartitioning", enabled);
    }

    // Nenhum grupo de domínios partilha cookies como se fosse um só site.
    [Fact]
    public void Preferences_TurnOffRelatedWebsiteSets()
        => Assert.Equal(false, PrivacyPreferences.Hardening["privacy_sandbox.first_party_sets_enabled"]);

    private static PrivacyPage.State State(Dictionary<string, string> args) => new(
        args, PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
        SecureDns: null, HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);

    [Fact]
    public void PrivacyPage_ShowsTheJarsAndTheReferrerFromTheRealCode()
    {
        var promises = PrivacyPage.Promises(State(new(CefConfigurator.Build("p").CefCommandLineArgs)));
        Assert.True(Assert.Single(promises, p => p.Id == "jars").Kept);
        var referrer = Assert.Single(promises, p => p.Id == "referrer");
        Assert.True(referrer.Kept);
        Assert.Contains("«https://www.exemplo.pt/»", referrer.Detail);
    }

    [Fact]
    public void PrivacyPage_MissingPartitionSwitch_IsBrokenAndNamed()
    {
        var args = new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs);
        args["enable-features"] = string.Join(',', EngineHardening.Switch(args, "enable-features").Where(f => f != "SplitCacheByNetworkIsolationKey"));
        var jars = Assert.Single(PrivacyPage.Promises(State(args)), p => p.Id == "jars");
        Assert.False(jars.Kept);
        Assert.Contains("SplitCacheByNetworkIsolationKey", jars.Detail);
    }
}
