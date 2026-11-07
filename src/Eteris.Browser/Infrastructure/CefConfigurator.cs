using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Infrastructure;

public static class CefConfigurator
{
    // Porta 9 («discard»): nada escuta em 127.0.0.1, por isso o updater do Chrome falha logo, sem sair do PC.
    public const string NoUpdateServer = "http://127.0.0.1:9/";

    public static CefSettings Build(string profilePath, bool blockAutoplay = true, bool muteAudio = false)
    {
        var settings = new CefSettings
        {
            CachePath = profilePath,
            Locale = "pt-PT",
            LogSeverity = LogSeverity.Disable,
        };
        settings.CefCommandLineArgs["disable-gpu-shader-disk-cache"] = "1";
        // Voltar/Avançar instantâneos (back/forward cache), como no Chrome: o CefSharp.Wpf desliga-o por omissão por
        // causa do modo fora da tela (OSR), que o Éteris não usa — a página vive numa janela própria (HwndHost).
        settings.CefCommandLineArgs.Remove("disable-back-forward-cache");
        // Só nos testes ao vivo (TestSwitches.Mute): nenhum som sai do motor.
        if (muteAudio) settings.CefCommandLineArgs["mute-audio"] = "1";
        // Sem o «component updater» do Chrome nem tráfego de fundo: de outro modo o motor descarrega sozinho, dos
        // servidores da Google, dezenas de componentes (Widevine, modelos de sugestão, Optimization Hints, Variations…).
        // Privacidade, e o módulo Widevine é da Google e tem os seus próprios termos.
        settings.CefCommandLineArgs["disable-component-update"] = "1";
        settings.CefCommandLineArgs["disable-background-networking"] = "1";
        // Pedidos «a pedido» do updater (ex.: o manifesto dos modelos de IA) passam ao lado do disable-component-update:
        // apontar o updater para um endereço local sem nada a escutar corta-os a todos.
        settings.CefCommandLineArgs["component-updater"] = "url-source=" + NoUpdateServer;
        // Nenhum site começa a tocar som sem um clique do utilizador (política do Chromium; aplica-se ao arrancar).
        if (blockAutoplay) settings.CefCommandLineArgs["autoplay-policy"] = "document-user-activation-required";
        // A descoberta de dispositivos Cast varre a rede local (SSDP/mDNS) e faz o Windows pedir exceção na firewall.
        settings.CefCommandLineArgs.TryGetValue("disable-features", out var existing);
        settings.CefCommandLineArgs["disable-features"] = string.Join(',',
            new[]
                {
                    existing, "MediaRouter", "DialMediaRouteProvider", "CastMediaRouteProvider",
                    // O «Optimization Guide» descarrega da Google, à margem do component updater, o manifesto dos
                    // modelos de IA no dispositivo (Gemini Nano/Gemma) e sugestões de otimização.
                    "OptimizationHints", "OptimizationHintsFetching", "OptimizationGuideModelDownloading",
                    "OptimizationGuideOnDeviceModel", "OnDeviceModelPerformanceParams",
                }
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
        ProfileCleanup.RemoveDownloadedWidevine(AppPaths.Profile);
        if (Data.Settings.Current.ClearOnExit) ProfileCleanup.RemoveSiteData(AppPaths.Profile);
        var dns = Data.Settings.Current.SecureDns;
        Shields.PrivacyPreferences.StartedWithSecureDns = LocalState.ApplySecureDns(
            System.IO.Path.Combine(AppPaths.Profile, "Local State"), Shields.SecureDns.ModeFor(dns), Shields.SecureDns.TemplateFor(dns) ?? string.Empty)
            && Shields.SecureDns.Providers.TryGetValue(dns, out var provider) ? provider.Name : null;
        Cef.Initialize(Build(AppPaths.Profile, Data.Settings.Current.BlockAutoplay, TestSwitches.Mute), performDependencyCheck: true, browserProcessHandler: null);
    }
}
