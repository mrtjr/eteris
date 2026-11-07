using CefSharp;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class CefConfiguratorTests
{
    [Fact]
    public void Build_SetsCachePathToGivenProfile()
        => Assert.Equal(@"C:\x\profile", CefConfigurator.Build(@"C:\x\profile").CachePath);

    [Fact]
    public void Build_UsesPortugueseLocale()
        => Assert.Equal("pt-PT", CefConfigurator.Build("p").Locale);

    [Fact]
    public void Build_BlocksAutoplayByDefault()
    {
        Assert.Equal("document-user-activation-required", CefConfigurator.Build("p").CefCommandLineArgs["autoplay-policy"]);
        Assert.False(CefConfigurator.Build("p", blockAutoplay: false).CefCommandLineArgs.ContainsKey("autoplay-policy"));
    }

    [Fact]
    public void Build_DisablesCefLogging()
        => Assert.Equal(LogSeverity.Disable, CefConfigurator.Build("p").LogSeverity);

    [Fact]
    public void Build_RegistersIsolatedSecureEterisScheme()
    {
        var scheme = Assert.Single(CefConfigurator.Build("p").CefCustomSchemes, s => s.SchemeName == "eteris");
        Assert.True(scheme.IsSecure);
        Assert.True(scheme.IsStandard);
        Assert.True(scheme.IsDisplayIsolated);
        Assert.NotNull(scheme.SchemeHandlerFactory);
    }

    [Fact]
    public void Build_DisablesLanDiscoveryOfCastDevices()
    {
        var features = CefConfigurator.Build("p").CefCommandLineArgs["disable-features"].Split(',');
        Assert.Contains("MediaRouter", features);
        Assert.Contains("DialMediaRouteProvider", features);
    }

    // O «HTTPS primeiro» do Chromium subia os pedidos antes do Éteris e falhava sem aviso: fica desligado.
    [Fact]
    public void Build_LeavesHttpsUpgradesToTheEteris()
        => Assert.Contains("HttpsUpgrades", CefConfigurator.Build("p").CefCommandLineArgs["disable-features"].Split(','));

    [Fact]
    public void Build_DoesNotSetMediaStreamSwitch()
        => Assert.False(CefConfigurator.Build("p").CefCommandLineArgs.ContainsKey("enable-media-stream"));
}
