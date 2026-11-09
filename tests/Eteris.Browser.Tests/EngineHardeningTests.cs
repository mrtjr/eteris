using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B4 «Promessas verificáveis»: GPC visível ao JavaScript e APIs de anúncios do Chromium desligadas.
public class EngineHardeningTests
{
    private static IReadOnlyList<string> Switch(string name) => EngineHardening.Switch(CefConfigurator.Build("p").CefCommandLineArgs, name);

    [Theory]
    [InlineData("BrowsingTopics")]
    [InlineData("Fledge")]
    [InlineData("ConversionMeasurement")]
    [InlineData("FencedFrames")]
    [InlineData("PrivacySandboxAdsAPIsOverride")]
    public void Build_DisablesTheChromiumAdApis(string feature) => Assert.Contains(feature, Switch("disable-features"));

    [Theory]
    [InlineData("TopicsAPI")]
    [InlineData("AdInterestGroupAPI")]
    [InlineData("SharedStorageAPI")]
    [InlineData("FencedFrames")]
    [InlineData("AttributionReporting")]
    public void Build_HidesTheAdApisFromJavaScript(string feature) => Assert.Contains(feature, Switch("disable-blink-features"));

    // As listas antigas (Cast, Optimization Guide, HTTPS-Upgrades) continuam lá: o Éteris junta, não substitui.
    [Fact]
    public void Build_KeepsTheEarlierDisabledFeatures()
    {
        var disabled = Switch("disable-features");
        Assert.Contains("MediaRouter", disabled);
        Assert.Contains("OptimizationHints", disabled);
        Assert.Contains("HttpsUpgrades", disabled);
        Assert.Equal(disabled.Count, disabled.Distinct().Count());
    }

    // Privacidade (medido com um proxy local, docs/paridade.md, secção 7): no arranque o Chromium pedia a hora a
    // clients2.google.com e a elegibilidade do «Modo IA» a www.google.com (com os cookies da Google). Fica fixa a lista
    // inteira do que o motor tem desligado e dos interruptores que o calam: tirar um nome tem de ser uma decisão.
    [Fact]
    public void Build_FixesEverySwitchThatKeepsTheEngineQuiet()
    {
        var args = CefConfigurator.Build("p").CefCommandLineArgs;
        Assert.Equal(
            new[]
            {
                "MediaRouter", "DialMediaRouteProvider", "CastMediaRouteProvider",
                "OptimizationHints", "OptimizationHintsFetching", "OptimizationGuideModelDownloading",
                "OptimizationGuideOnDeviceModel", "OnDeviceModelPerformanceParams", "HttpsUpgrades",
                "BrowsingTopics", "Fledge", "ConversionMeasurement", "FencedFrames",
                "PrivacySandboxAdsAPIsOverride", "PrivacySandboxAdsAPIsM1Override",
                "WebRtcHideLocalIpsWithMdns",
                "NetworkTimeServiceQuerying", "AimEnabled",
                "AutofillServerCommunication",
            }.Order(StringComparer.Ordinal),
            Switch("disable-features").Order(StringComparer.Ordinal));
        Assert.Equal("1", args["disable-background-networking"]);
        Assert.Equal("1", args["disable-component-update"]);
        Assert.Equal("url-source=" + CefConfigurator.NoUpdateServer, args["component-updater"]);
    }

    [Theory]
    [InlineData("NetworkTimeServiceQuerying")]
    [InlineData("AimEnabled")]
    public void Build_SendsNothingToGoogleAtStartup(string feature)
    {
        Assert.Contains(feature, Switch("disable-features"));
        Assert.Contains(EngineHardening.NoGoogleAtStartup, f => f.Feature == feature && !string.IsNullOrWhiteSpace(f.What));
    }

    // Revisão aos 50: a política de IP do WebRTC na linha de comandos (preferência fixa desde o arranque, também na janela
    // privada) e sem o respondedor mDNS do WebRTC, que abria UDP 5353 em todas as interfaces (aviso da firewall).
    [Fact]
    public void Build_FixesTheWebRtcPolicy_AndOpensNoListeningSockets()
    {
        var args = CefConfigurator.Build("p").CefCommandLineArgs;
        Assert.Equal("disable_non_proxied_udp", args["webrtc-ip-handling-policy"]);
        Assert.Contains("WebRtcHideLocalIpsWithMdns", Switch("disable-features"));
        Assert.All(EngineHardening.NoListeningSockets, f => Assert.False(string.IsNullOrWhiteSpace(f.What)));
    }

    // Protegido é o valor em vigor no perfil (lido de volta), não o êxito do SetPreference, que a linha de comandos recusa.
    [Theory]
    [InlineData("disable_non_proxied_udp", true)]
    [InlineData("default_public_interface_only", false)]
    [InlineData("default", false)]
    [InlineData(null, false)]
    [InlineData(1, false)]
    public void WebRtcProtected_IsTheEffectivePolicy(object? value, bool expected)
        => Assert.Equal(expected, PrivacyPreferences.IsWebRtcProtected(value));

    [Fact]
    public void Preferences_ReadTheWebRtcPolicyBack()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Shields", "PrivacyPreferences.cs"));
        Assert.Contains("var webRtc = IsWebRtcProtected(target.GetPreference(WebRtcPreference));", source);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Apply_MergesWithoutDuplicates()
    {
        var args = new Dictionary<string, string> { ["disable-features"] = "MediaRouter,Fledge" };
        EngineHardening.Apply(args);
        var features = args["disable-features"].Split(',');
        Assert.Equal("MediaRouter", features[0]);
        Assert.Single(features, f => f == "Fledge");
        Assert.Contains("BrowsingTopics", features);
    }

    [Fact]
    public void Apply_RecordsWhatTheEngineReceived()
    {
        var args = new Dictionary<string, string>();
        EngineHardening.Apply(args);
        Assert.Contains("BrowsingTopics", EngineHardening.Switch(EngineHardening.Applied, "disable-features"));
    }

    // Cada interruptor diz o que faz (aparece em eteris://privacy/).
    [Fact]
    public void AdApis_AreExplained()
        => Assert.All(EngineHardening.AdApis, a => Assert.False(string.IsNullOrWhiteSpace(a.What)));

    [Fact]
    public void Preferences_TurnOffTheAdApisInTheProfile()
    {
        Assert.Equal(false, PrivacyPreferences.Hardening["privacy_sandbox.m1.topics_enabled"]);
        Assert.Equal(false, PrivacyPreferences.Hardening["privacy_sandbox.m1.fledge_enabled"]);
        Assert.Equal(false, PrivacyPreferences.Hardening["privacy_sandbox.m1.ad_measurement_enabled"]);
    }

    [Fact]
    public void Headers_SendGpc_ButNotDoNotTrack()
    {
        Assert.Contains(("Sec-GPC", "1"), PrivacySignals.Headers);
        Assert.DoesNotContain(PrivacySignals.Headers, h => h.Name == "DNT");
    }

    [Theory]
    [InlineData("https://www.publico.pt/", true)]
    [InlineData("http://neverssl.com/", true)]
    [InlineData("eteris://privacy/", true)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("nao é endereço", false)]
    public void GpcScript_AppliesToSitesAndInternalPages(string url, bool expected)
        => Assert.Equal(expected, PrivacySignals.ScriptFor(url) is not null);

    [Fact]
    public void GpcScript_DefinesTheNavigatorPropertyAsTrue()
    {
        Assert.Contains("Navigator.prototype", PrivacySignals.GpcScript);
        Assert.Contains("'globalPrivacyControl'", PrivacySignals.GpcScript);
        Assert.Contains("return true;", PrivacySignals.GpcScript);
        // Um erro (propriedade congelada por outro script) não pode partir a página.
        Assert.Contains("catch", PrivacySignals.GpcScript);
    }

    [Fact]
    public void Compose_PutsTheSignalFirstAndSkipsEmptyParts()
    {
        Assert.Null(PrivacySignals.Compose(null, "", null));
        Assert.Equal("a\n;\nb", PrivacySignals.Compose("a", null, "b"));
        var script = PrivacySignals.Compose(PrivacySignals.ScriptFor("https://a.pt/"), "scriptlets()", "extra()")!;
        Assert.True(script.IndexOf("globalPrivacyControl", StringComparison.Ordinal) < script.IndexOf("scriptlets()", StringComparison.Ordinal));
        Assert.EndsWith("extra()", script);
    }

    // Recarregar ou ir a outra página do mesmo site não volta a registar o mesmo script: retirar e registar durante a
    // navegação só é atendido depois de o documento nascer, e a página (sempre, na janela privada) ficava sem ele.
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "a()", true)]
    [InlineData("a()", "a()", false)]
    [InlineData("a()", "b()", true)]
    [InlineData("a()", null, true)]
    public void Injector_ReplacesOnlyWhenTheScriptChanges(string? wanted, string? script, bool expected)
        => Assert.Equal(expected, ScriptletInjector.NeedsReplace(wanted, script));
}
