using System.IO;
using CefSharp;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class CefConfiguratorTests
{
    [Fact]
    public void Build_SetsCachePathToGivenProfile()
        => Assert.Equal(@"C:\x\profile", CefConfigurator.Build(@"C:\x\profile").CachePath);

    // A língua do motor é a dos sites: o Intl das páginas dá o mesmo que o navigator.language.
    [Fact]
    public void Build_UsesTheSiteLanguageAsTheEngineLocale()
    {
        var settings = CefConfigurator.Build("p");
        Assert.Equal(settings.AcceptLanguageList, settings.Locale);
        Assert.Equal(Eteris.Browser.Shields.PrivacyPreferences.SiteLanguage, settings.Locale);
    }

    [Fact]
    public void Build_BlocksAutoplayByDefault()
    {
        Assert.Equal("document-user-activation-required", CefConfigurator.Build("p").CefCommandLineArgs["autoplay-policy"]);
        Assert.False(CefConfigurator.Build("p", blockAutoplay: false).CefCommandLineArgs.ContainsKey("autoplay-policy"));
    }

    [Fact]
    public void Build_MutesAudioOnlyForLiveTests()
    {
        Assert.False(CefConfigurator.Build("p").CefCommandLineArgs.ContainsKey("mute-audio"));
        Assert.Equal("1", CefConfigurator.Build("p", muteAudio: true).CefCommandLineArgs["mute-audio"]);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TestMuteSwitch_IsOnlyOnForOne(string? value, bool expected)
        => Assert.Equal(expected, Eteris.Browser.Infrastructure.TestSwitches.IsOn(value));

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
    }

    // O «HTTPS primeiro» do Chromium subia os pedidos antes do Éteris e falhava sem aviso: fica desligado.
    [Fact]
    public void Build_LeavesHttpsUpgradesToTheEteris()
        => Assert.Contains("HttpsUpgrades", CefConfigurator.Build("p").CefCommandLineArgs["disable-features"].Split(','));

    [Fact]
    public void Build_DoesNotSetMediaStreamSwitch()
        => Assert.False(CefConfigurator.Build("p").CefCommandLineArgs.ContainsKey("enable-media-stream"));

    // Voltar/Avançar pela cache de páginas, como no Chrome: o CefSharp.Wpf desliga-a por omissão (por causa do OSR).
    [Fact]
    public void Build_KeepsTheBackForwardCache()
        => Assert.False(CefConfigurator.Build("p").CefCommandLineArgs.ContainsKey("disable-back-forward-cache"));

    // O Cef.Initialize corre noutra thread (InitializeInBackground), e o Cef.Shutdown tem de correr nela: o fecho é do
    // Éteris. O do próprio CefSharp.Wpf (no fecho do Dispatcher do fio da interface) fica desligado, e os browsers que
    // ainda existam libertam-se no fio da interface (Cef.PreShutdown) antes do Shutdown na outra thread; o base.OnExit
    // corre mesmo que o fecho do motor falhe.
    [Fact]
    public void Shutdown_IsOursAndReleasesBrowsersOnTheUiThreadFirst()
    {
        var configurator = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Infrastructure", "CefConfigurator.cs"));
        var initialize = configurator.IndexOf("public static void InitializeInBackground", StringComparison.Ordinal);
        Assert.True(configurator.IndexOf("CefSharpSettings.ShutdownOnExit = false;", initialize, StringComparison.Ordinal)
                    < configurator.IndexOf("_thread.Start();", initialize, StringComparison.Ordinal));
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "App.xaml.cs"));
        var exit = app[app.IndexOf("protected override void OnExit", StringComparison.Ordinal)..];
        var pre = exit.IndexOf("Cef.PreShutdown();", StringComparison.Ordinal);
        Assert.InRange(pre, 0, exit.IndexOf("CefConfigurator.Shutdown();", StringComparison.Ordinal));
        Assert.True(exit.IndexOf("finally", pre, StringComparison.Ordinal) < exit.IndexOf("base.OnExit(e);", StringComparison.Ordinal));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
