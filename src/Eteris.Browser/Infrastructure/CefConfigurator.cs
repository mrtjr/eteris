using CefSharp;
using CefSharp.Wpf;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Infrastructure;

public static class CefConfigurator
{
    public static CefSettings Build(string profilePath)
    {
        var settings = new CefSettings
        {
            CachePath = profilePath,
            Locale = "pt-PT",
            LogSeverity = LogSeverity.Disable,
        };
        settings.CefCommandLineArgs["disable-gpu-shader-disk-cache"] = "1";
        settings.RegisterScheme(new CefCustomScheme
        {
            SchemeName = EterisSchemeHandlerFactory.SchemeName,
            SchemeHandlerFactory = new EterisSchemeHandlerFactory(),
            IsStandard = true,
            IsSecure = true,
            IsDisplayIsolated = true,
        });
        return settings;
    }

    public static void Initialize()
    {
        AppPaths.EnsureDirectories();
        Cef.Initialize(Build(AppPaths.Profile), performDependencyCheck: true, browserProcessHandler: null);
    }
}
