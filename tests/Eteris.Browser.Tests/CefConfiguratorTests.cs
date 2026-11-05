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
    public void Build_DisablesCefLogging()
        => Assert.Equal(LogSeverity.Disable, CefConfigurator.Build("p").LogSeverity);

    [Fact]
    public void Build_DoesNotSetMediaStreamSwitch()
        => Assert.False(CefConfigurator.Build("p").CefCommandLineArgs.ContainsKey("enable-media-stream"));
}
