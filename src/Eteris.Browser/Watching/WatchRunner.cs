using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Interop;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Watching;

// O motor da vigia: um browser escondido numa janela própria fora do ecrã, com o tamanho de uma janela normal (dentro da
// janela principal minimizada o motor ficava com 14×14 px e havia sites que o tratavam como telemóvel; estudo da etapa A).
// Visita uma página, espera até 8 s que o preço ou o pedaço apareça (a cada 1 s), lê e devolve; nunca lança.
//
// - Sem imagens, sem som; com JavaScript (muitas lojas só põem o preço com ele) e com o Escudo (o tratador de pedidos vem
//   de quem cria o motor: MainWindow.Watch.cs dá-lhe o PrivacyRequestHandler).
// - Recusa transferências, janelas novas, diálogos de JavaScript e pedidos de permissão (Browser/SilentHandlers.cs).
// - Um endereço fora da web (meli://, um programa) é cancelado antes de abrir e conta como salto para uma aplicação.
// - O contexto vem de «contextFor»: o só em memória (sem os cookies do utilizador) ou o do perfil («com a minha sessão»).
//   O só em memória é do motor: sai no Release, e cada lote começa sem cookies. O do perfil nunca se larga aqui.
// - Corre no fio da interface (o browser e a janela são WPF). Uma vigia de cada vez: quem chama espera por cada leitura.
public sealed class WatchRunner(Func<bool, IRequestContext> contextFor, Func<IRequestHandler> requestHandler) : IDisposable
{
    private static readonly TimeSpan CreateLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SettleStep = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ScriptLimit = TimeSpan.FromSeconds(5);

    private readonly Func<bool, IRequestContext> _contextFor = contextFor;
    private readonly Func<IRequestHandler> _requestHandler = requestHandler;
    private Window? _host;
    private ChromiumWebBrowser? _browser;
    private AppRedirectGuard? _guard;
    private IRequestContext? _context;
    private bool _useSession;
    private bool _visited;

    public Task<(PageReading? Reading, PageBlock Block)> ReadAsync(string url, string? selector, bool useSession, CancellationToken ct)
        => ReadAsync(url, selector, WatchKind.Price, useSession, ct);

    // «kind» decide quando a espera acaba (WatchRules.Settled): numa vigia de preço, um pedaço ainda com o marcador não serve.
    public async Task<(PageReading? Reading, PageBlock Block)> ReadAsync(string url, string? selector, WatchKind kind, bool useSession,
        CancellationToken ct)
    {
        try
        {
            if (await EnsureAsync(useSession, ct) is not { } browser || _guard is not { } guard) return (null, PageBlock.HttpError);
            // A página da vigia anterior sai antes: uma carga que não chegue a acontecer nunca é lida no lugar desta.
            if (_visited) await LoadAsync(browser, "about:blank", ct);
            _visited = true;
            guard.Reset();

            var (finished, response) = await LoadAsync(browser, url, ct);
            if (guard.AppUrl is { } app) return (null, WatchRules.Classify(0, url, app, "", false));
            // Sem rede, nome que não resolve, transferência recusada: a página não carregou.
            if (finished && response is { } r && r.ErrorCode != CefErrorCode.None) return (null, PageBlock.HttpError);
            var status = finished && response is { } ok ? ok.HttpStatusCode : 0;

            PageReading? reading = null;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var result = await PageScript.EvaluateAsync(browser, WatchScripts.Read(selector), ScriptLimit);
                if (guard.AppUrl is { } later) return (null, WatchRules.Classify(0, url, later, "", false));
                if (result.Success && result.Result is string json) reading = WatchReadingParser.Parse(json, browser.Address ?? url);
                if (reading is not null && WatchRules.Settled(kind, reading)) break;
                if (clock.Elapsed >= SettleLimit) break;
                await Task.Delay(SettleStep, ct);
            }

            var finalUrl = browser.Address ?? url;
            if (reading is not null) reading = reading with { FinalUrl = finalUrl };
            return (reading, WatchRules.Classify(status, url, finalUrl, reading?.Title ?? "", reading?.ChallengeMarker == true));
        }
        catch (Exception)
        {
            // Cancelada (a janela fechou) ou o motor caiu a meio: uma falha passageira, nunca uma exceção para quem chama.
            return (null, PageBlock.HttpError);
        }
    }

    // Larga o motor, a janela e o contexto só em memória (fim do lote).
    public void Release()
    {
        if (_browser is { } browser)
        {
            _browser = null;
            PageScript.Release(browser);
            try { if (!browser.IsDisposed) browser.Dispose(); } catch (Exception) { }
        }
        _guard = null;
        if (_host is { } host)
        {
            _host = null;
            try { host.Close(); } catch (Exception) { }
        }
        if (_context is { } context)
        {
            _context = null;
            if (!_useSession) try { context.Dispose(); } catch (Exception) { }
        }
        _visited = false;
    }

    public void Dispose() => Release();

    private static async Task<(bool Finished, LoadUrlAsyncResponse? Response)> LoadAsync(ChromiumWebBrowser browser, string url, CancellationToken ct)
    {
        var load = browser.LoadUrlAsync(url);
        var done = await Task.WhenAny(load, Task.Delay(LoadLimit, ct));
        ct.ThrowIfCancellationRequested();
        return done == load && load.IsCompletedSuccessfully ? (true, load.Result) : (false, null);
    }

    // O browser nasce com a janela à vista (fora do ecrã): sem a janela, o motor nem chega a existir. Um lote que mude
    // de contexto (uma vigia «com a minha sessão» a seguir a uma sem) começa outro motor.
    private async Task<ChromiumWebBrowser?> EnsureAsync(bool useSession, CancellationToken ct)
    {
        if (_browser is not null && _useSession != useSession) Release();
        if (_browser is { IsDisposed: false } existing) return existing;

        _useSession = useSession;
        _context = _contextFor(useSession);
        var browser = new ChromiumWebBrowser("about:blank")
        {
            BrowserSettings = new BrowserSettings { ImageLoading = CefState.Disabled },
            RequestContext = _context,
            RequestHandler = new AppRedirectGuard(_requestHandler()),
            FocusHandler = new SilentFocusHandler(),
            LifeSpanHandler = new PopupToTabHandler(_ => { }),
            DownloadHandler = new SilentDownloadHandler(() => { }),
            MenuHandler = new SilentMenuHandler(),
            JsDialogHandler = new SilentDialogHandler(),
            PermissionHandler = new SilentPermissionHandler(),
            Focusable = false,
            IsHitTestVisible = false,
        };
        _guard = (AppRedirectGuard)browser.RequestHandler;
        // Conteúdo web confirmado: a rede de segurança do sandbox deixa de contar quedas dos filhos (INF-01), como nas abas.
        browser.FrameLoadStart += (_, e) =>
        {
            if (e.Frame.IsMain && Infrastructure.SandboxFallback.IsWebContent(e.Url)) Infrastructure.StartupGuard.Current.WebContentCommitted();
        };
        _browser = browser;
        _host = HostWindow();
        _host.Content = browser;
        _host.Show();

        var created = browser.WaitForInitialLoadAsync();
        if (await Task.WhenAny(created, Task.Delay(CreateLimit, ct)) != created)
        {
            // O motor não nasceu a tempo: sai já (janela incluída), e a vigia seguinte tenta um novo.
            Release();
            return null;
        }
        ct.ThrowIfCancellationRequested();
        browser.GetBrowserHost()?.SetAudioMuted(true);
        return browser;
    }

    // Fora do ecrã, sem botão na barra de tarefas nem no Alt+Tab (WS_EX_TOOLWINDOW), nunca ativada, nunca minimizada.
    // Sem dona: uma janela com dona minimiza-se com ela.
    private static Window HostWindow()
    {
        var host = new Window
        {
            Title = "Vigia do Éteris",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 1280,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
        };
        host.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(host).Handle;
            SetWindowLongPtr(handle, ExStyle, GetWindowLongPtr(handle, ExStyle) | ToolWindow | NoActivate);
        };
        host.StateChanged += (_, _) =>
        {
            if (host.WindowState != WindowState.Normal) host.WindowState = WindowState.Normal;
        };
        return host;
    }

    private const int ExStyle = -20;
    private const nint ToolWindow = 0x80, NoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

// O tratador de pedidos do motor da vigia: o de quem o criou (o Escudo), com três recusas à frente. Um endereço fora da web
// (WatchRules.IsAppScheme) é cancelado antes de abrir — na página principal, fica anotado para a leitura contar como
// salto para uma aplicação —, e os links para abrir noutra aba não abrem. Corre nas threads do CEF: nenhuma exceção sai.
internal sealed class AppRedirectGuard(IRequestHandler inner) : IRequestHandler
{
    private string? _appUrl;

    // O endereço fora da web para onde a página principal quis ir, desde o último Reset.
    public string? AppUrl => Volatile.Read(ref _appUrl);

    public void Reset() => Volatile.Write(ref _appUrl, null);

    public bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        try
        {
            if (WatchRules.IsAppScheme(request.Url))
            {
                if (IsMain(frame)) Volatile.Write(ref _appUrl, request.Url);
                return true;
            }
        }
        catch (Exception) { return true; }
        return inner.OnBeforeBrowse(chromiumWebBrowser, browser, frame, request, userGesture, isRedirect);
    }

    // Um frame destacado lança ao ler as propriedades (ver SafeFrame).
    private static bool IsMain(IFrame? frame)
    {
        try { return frame is { IsValid: true, IsMain: true }; }
        catch (Exception) { return false; }
    }

    public bool OnOpenUrlFromTab(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl,
        WindowOpenDisposition targetDisposition, bool userGesture) => true;

    public IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
        bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => inner.GetResourceRequestHandler(chromiumWebBrowser, browser, frame, request, isNavigation, isDownload, requestInitiator,
            ref disableDefaultHandling);

    public bool GetAuthCredentials(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, bool isProxy, string host, int port,
        string realm, string scheme, IAuthCallback callback)
        => inner.GetAuthCredentials(chromiumWebBrowser, browser, originUrl, isProxy, host, port, realm, scheme, callback);

    public bool OnCertificateError(IWebBrowser chromiumWebBrowser, IBrowser browser, CefErrorCode errorCode, string requestUrl,
        ISslInfo sslInfo, IRequestCallback callback)
        => inner.OnCertificateError(chromiumWebBrowser, browser, errorCode, requestUrl, sslInfo, callback);

    public bool OnSelectClientCertificate(IWebBrowser chromiumWebBrowser, IBrowser browser, bool isProxy, string host, int port,
        X509Certificate2Collection certificates, ISelectClientCertificateCallback callback)
        => inner.OnSelectClientCertificate(chromiumWebBrowser, browser, isProxy, host, port, certificates, callback);

    public void OnRenderViewReady(IWebBrowser chromiumWebBrowser, IBrowser browser)
        => inner.OnRenderViewReady(chromiumWebBrowser, browser);

    public void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status, int errorCode,
        string errorMessage)
        => inner.OnRenderProcessTerminated(chromiumWebBrowser, browser, status, errorCode, errorMessage);

    public void OnDocumentAvailableInMainFrame(IWebBrowser chromiumWebBrowser, IBrowser browser)
        => inner.OnDocumentAvailableInMainFrame(chromiumWebBrowser, browser);
}
