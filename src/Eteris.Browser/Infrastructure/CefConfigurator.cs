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
        // A descoberta de dispositivos Cast varre a rede local (SSDP/mDNS) e faz o Windows pedir exceção na firewall.
        settings.CefCommandLineArgs.TryGetValue("disable-features", out var existing);
        settings.CefCommandLineArgs["disable-features"] = string.Join(',',
            new[] { existing, "MediaRouter", "DialMediaRouteProvider", "CastMediaRouteProvider" }
                .Where(f => !string.IsNullOrEmpty(f)));
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
