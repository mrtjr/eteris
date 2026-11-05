using CefSharp;
using CefSharp.Wpf;

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
        settings.CefCommandLineArgs["enable-media-stream"] = "false";
        settings.CefCommandLineArgs["disable-gpu-shader-disk-cache"] = "1";
        return settings;
    }

    public static void Initialize()
    {
        AppPaths.EnsureDirectories();
        Cef.Initialize(Build(AppPaths.Profile), performDependencyCheck: true, browserProcessHandler: null);
    }
}
