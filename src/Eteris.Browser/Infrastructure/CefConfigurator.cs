using CefSharp;
using CefSharp.Wpf;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Infrastructure;

public static class CefConfigurator
{
    public static CefSettings Build(string profilePath, bool blockAutoplay = true)
    {
        var settings = new CefSettings
        {
            CachePath = profilePath,
            Locale = "pt-PT",
            LogSeverity = LogSeverity.Disable,
        };
        settings.CefCommandLineArgs["disable-gpu-shader-disk-cache"] = "1";
        // Nenhum site começa a tocar som sem um clique do utilizador (política do Chromium; aplica-se ao arrancar).
        if (blockAutoplay) settings.CefCommandLineArgs["autoplay-policy"] = "document-user-activation-required";
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
        ProfileCleanup.RemoveChromiumHistory(AppPaths.Profile);
        var dns = Data.Settings.Current.SecureDns;
        Shields.PrivacyPreferences.StartedWithSecureDns = LocalState.ApplySecureDns(
            System.IO.Path.Combine(AppPaths.Profile, "Local State"), Shields.SecureDns.ModeFor(dns), Shields.SecureDns.TemplateFor(dns) ?? string.Empty)
            && Shields.SecureDns.Providers.TryGetValue(dns, out var provider) ? provider.Name : null;
        Cef.Initialize(Build(AppPaths.Profile, Data.Settings.Current.BlockAutoplay), performDependencyCheck: true, browserProcessHandler: null);
    }
}
