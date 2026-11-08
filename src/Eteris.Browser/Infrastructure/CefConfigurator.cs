using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Infrastructure;

public static class CefConfigurator
{
    // Porta 9 («discard»): nada escuta em 127.0.0.1, por isso o updater do Chrome falha logo, sem sair do PC.
    public const string NoUpdateServer = "http://127.0.0.1:9/";

    public static CefSettings Build(string profilePath, bool blockAutoplay = true, bool muteAudio = false, nint sandboxInfo = 0,
                                    int? devToolsPort = null, string? bootstrapExe = null, bool networkSandbox = false)
    {
        var settings = new CefSettings
        {
            CachePath = profilePath,
            // Locale: a língua dos sites (EngineHardening.Configure, mais abaixo).
            LogSeverity = LogSeverity.Disable,
        };
        // Sandbox dos processos das páginas (docs/sandbox.md, passo D): só com o ponteiro do bootstrap da CEF e o CefSharp
        // com o patch v2 (build/cefsharp; ETERIS_CEFSHARP_SANDBOX). Os filhos passam a ser o próprio exe (o bootstrap).
        var sandboxOn = SandboxSupported && sandboxInfo != 0;
#if ETERIS_CEFSHARP_SANDBOX
        if (sandboxOn) settings.WindowsSandboxInfo = sandboxInfo;
#endif
        // Pelo bootstrap mas sem sandbox (--eteris-no-sandbox, ou o CefSharp oficial): os filhos continuam a ser o próprio
        // exe (a DLL nativa), não o processo filho .NET, que a publicação com sandbox já não traz.
        if (!sandboxOn && bootstrapExe is not null) settings.BrowserSubprocessPath = bootstrapExe;
        // Só nos testes ao vivo (TestSwitches.DevToolsPort): DevTools do motor em 127.0.0.1.
        if (devToolsPort is { } port) settings.RemoteDebuggingPort = port;
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
                    // O «HTTPS primeiro» é do Éteris (Shields/HttpsFirst.cs, com aviso): o do Chromium subia o pedido
                    // antes de o tratador o ver e recuava em silêncio para http://. O recuo rápido (~3 s) que ele tinha
                    // é agora do Éteris: PrivacyRequestHandler.WatchUpgrade sonda o http:// e mostra o aviso (-7).
                    "HttpsUpgrades",
                }
                .Where(f => !string.IsNullOrEmpty(f)));
        // Serviço de rede também em sandbox (AppContainer): só com as páginas em sandbox e a ACL da pasta confirmada
        // (NetworkSandbox.Decide). O EngineHardening, a seguir, junta-lhe as funcionalidades dele.
        if (networkSandbox) settings.CefCommandLineArgs["enable-features"] = NetworkSandbox.Feature;
        // Promessas do Éteris na linha de comandos do motor (APIs de anúncios desligadas…): Shields/EngineHardening.cs.
        Shields.EngineHardening.Configure(settings);
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

    // Tudo o que tem de estar feito antes do Cef.Initialize (pastas, limpeza do perfil, Local State, definições do motor).
    public static CefSettings Prepare()
    {
        AppPaths.EnsureDirectories();
        StartupTrace.Mark("pastas");
        ProfileCleanup.RemoveChromiumHistory(AppPaths.Profile);
        ProfileCleanup.RemoveDownloadedWidevine(AppPaths.Profile);
        StartupTrace.Mark("limpeza");
        if (Data.Settings.Current.ClearOnExit) ProfileCleanup.RemoveSiteData(AppPaths.Profile);
        StartupTrace.Mark("definicoes");
        var dns = Data.Settings.Current.SecureDns;
        Shields.PrivacyPreferences.StartedWithSecureDns = LocalState.ApplySecureDns(
            System.IO.Path.Combine(AppPaths.Profile, "Local State"), Shields.SecureDns.ModeFor(dns), Shields.SecureDns.TemplateFor(dns) ?? string.Empty)
            && Shields.SecureDns.Providers.TryGetValue(dns, out var provider) ? provider.Name : null;
        Shields.PrivacyPreferences.SecureDnsTemplate = Shields.PrivacyPreferences.StartedWithSecureDns is null ? null : Shields.SecureDns.TemplateFor(dns);
        StartupTrace.Mark("localstate");
        // Rede de segurança (Infrastructure/SandboxFallback.cs): o marcador «a arrancar» fica no perfil antes do motor
        // arrancar; dois arranques com sandbox falhados seguidos desligam-no nesta versão, como --eteris-no-sandbox.
        var args = Environment.GetCommandLineArgs();
        var available = SandboxSupported ? SandboxInfo.Available : 0;
        var guard = StartupGuard.ForProfile(AppPaths.Root);
        var start = guard.Begin(Browser.SettingsPage.Version, available != 0, SandboxInfo.Disabled(args),
            args.Any(a => a.Equals(SandboxFallback.RetrySwitch, StringComparison.OrdinalIgnoreCase)));
        var sandbox = start.UseSandbox ? available : 0;
        SandboxActive = sandbox != 0;
        guard.WatchChildren();
        StartupTrace.Mark("rede.seguranca", (start.UseSandbox ? "sandbox" : "sem-sandbox") + " " + start.Notice);
        var bootstrap = SandboxInfo.LaunchedByBootstrap ? Environment.ProcessPath : null;
        // Pelo bootstrap não há processo filho .NET (CefSharp.BrowserSubprocess): o DependencyChecker procurá-lo-ia.
        _dependencyCheck = bootstrap is null;
        var networkSandbox = NetworkSandbox.Decide(sandbox != 0, Environment.ProcessPath);
        // O perfil do AppContainer que o Chromium vai criar para este caminho: a desinstalação apaga-o.
        if (networkSandbox) NetworkSandbox.Remember(Environment.ProcessPath);
        var settings = Build(AppPaths.Profile, Data.Settings.Current.BlockAutoplay, TestSwitches.Mute, sandbox, TestSwitches.DevToolsPort, bootstrap, networkSandbox);
        StartupTrace.Mark("cef.settings");
        return settings;
    }

    // O Cef.Initialize (~100 ms no arranque) corre numa thread própria enquanto o fio da interface cria a janela (o XAML
    // leva ~280 ms); quem precisa do motor (o 1.º browser, o contexto privado) espera por WaitReady. O CefSharp exige o
    // Cef.Shutdown na mesma thread do Cef.Initialize: a thread fica à espera dele (Shutdown leva-o lá). «ready» corre
    // nela logo a seguir à inicialização (o que precisa do contexto global).
    private static Thread? _thread;
    private static readonly ManualResetEventSlim Ready = new(false);
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Work = [];
    // Uma falha do Cef.Initialize volta a ser lançada no fio da interface (WaitReady), como quando ele corria lá.
    private static System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;
    // Falso quando o Éteris arrancou pelo bootstrap do sandbox (Prepare).
    private static bool _dependencyCheck = true;

    public static void InitializeInBackground(CefSettings settings, Action ready)
    {
        // O fecho do motor é do Éteris (App.OnExit: PreShutdown no fio da interface, Shutdown nesta thread). Sem isto, o
        // CefSharp.Wpf prendia o seu próprio Cef.Shutdown ao fecho do Dispatcher do fio da interface, que não é a thread do
        // Cef.Initialize: hoje não fazia nada (o motor já estava fechado), mas lançava se o nosso fecho não tivesse corrido.
        // Antes de nascer qualquer ChromiumWebBrowser (o construtor estático do CefSharp lê-o).
        CefSharpSettings.ShutdownOnExit = false;
        _thread = new Thread(() =>
        {
            try
            {
                Cef.Initialize(settings, performDependencyCheck: _dependencyCheck, browserProcessHandler: null);
                StartupTrace.Mark("cef.initialize");
                ready();
            }
            catch (Exception ex) { _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            finally { Ready.Set(); }
            foreach (var action in Work.GetConsumingEnumerable()) action();
        })
        { IsBackground = true, Name = "Éteris: arranque do motor" };
        // STA, como o fio da interface onde o Cef.Initialize corria: as esperas numa thread STA recebem as mensagens COM.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    // Espera pelo fim do Cef.Initialize (já acabou, ou nunca foi para outra thread: não espera).
    public static void WaitReady()
    {
        if (_thread is null) return;
        if (!Ready.IsSet)
        {
            Ready.Wait();
            StartupTrace.Mark("cef.pronto");
        }
        _failure?.Throw();
    }

    // O Cef.Shutdown liberta os ChromiumWebBrowser ainda registados, na thread onde corre: quem chama faz antes o
    // Cef.PreShutdown no fio da interface (App.OnExit), que os liberta lá, onde as janelas deles vivem.
    public static void Shutdown()
    {
        if (_thread is null)
        {
            Cef.Shutdown();
            return;
        }
        Ready.Wait();
        using var done = new ManualResetEventSlim(false);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        Work.Add(() =>
        {
            try { Cef.Shutdown(); }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        });
        done.Wait();
        failure?.Throw();
    }

    // O sandbox só liga com o CefSharp do Éteris (patch v2); com o oficial o ponteiro é ignorado.
    public static bool SandboxSupported =>
#if ETERIS_CEFSHARP_SANDBOX
        true;
#else
        false;
#endif

    public static bool SandboxEnabled => SandboxSupported && SandboxInfo.Pointer != 0;

    // Este arranque corre mesmo com o sandbox (depois da rede de segurança; Prepare).
    public static bool SandboxActive { get; private set; }
}
