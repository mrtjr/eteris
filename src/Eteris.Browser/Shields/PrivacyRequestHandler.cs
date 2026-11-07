using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Shields;

public sealed class PrivacyRequestHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Func<IBrowser, string, Task> onNavigate, Action<string> onBlocked,
    Action? onCrashed = null, string? userAgent = null)
    : CefSharp.Handler.RequestHandler
{
    // Cabeçalhos de um pedido feito «como telemóvel» (vista móvel de um site fixado na barra lateral): o agente e
    // as dicas de cliente que o acompanham, para o servidor não ver um Android a dizer-se Windows.
    public static IReadOnlyList<(string Name, string Value)> MobileHeaders(string userAgent) =>
    [
        ("User-Agent", userAgent),
        ("Sec-CH-UA-Mobile", "?1"),
        ("Sec-CH-UA-Platform", "\"Android\""),
    ];

    // O processo da página morreu (crash, falta de memória): a janela mostra a página de erro em vez de um ecrã vazio.
    protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status,
        int errorCode, string errorMessage)
        => onCrashed?.Invoke();

    private readonly PrivacyResourceHandler _resources = new(blocker, isProtected, onNavigate, onBlocked, userAgent);

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => _resources;
}

// Decide cada pedido do separador: cabeçalhos de privacidade, limpeza e HTTPS do endereço da página, modo rigoroso e
// listas de bloqueio. Corre na thread IO do CEF: nenhuma exceção pode sair daqui (fechava o Éteris inteiro).
internal sealed class PrivacyResourceHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Func<IBrowser, string, Task> onNavigate,
    Action<string> onBlocked, string? userAgent)
    : CefSharp.Handler.ResourceRequestHandler
{
    protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IRequest request, IRequestCallback callback)
        => Decide(browser, frame, request, callback);

    internal CefReturnValue Decide(IBrowser browser, IFrame? frame, IRequest request, IRequestCallback callback)
    {
        var blocking = false;
        try
        {
            var url = request.Url;
            request.SetHeaderByName("DNT", "1", true);
            request.SetHeaderByName("Sec-GPC", "1", true);
            if (userAgent is not null)
                foreach (var (name, value) in PrivacyRequestHandler.MobileHeaders(userAgent)) request.SetHeaderByName(name, value, true);

            if (request.ResourceType == ResourceType.MainFrame)
            {
                // Links sem parâmetros de rastreio (utm_*, fbclid…): o pedido sai já limpo.
                if (Data.Settings.Current.CleanUrls && UrlCleaner.Clean(url) is var clean && clean != url)
                {
                    request.Url = clean;
                    return CefReturnValue.Continue;
                }
                if (Data.Settings.Current.HttpsFirst && ShieldEngine.Https.Upgrade(url) is { } secure)
                {
                    request.Url = secure;
                    return CefReturnValue.Continue;
                }
                // O pedido da página só sai depois de os scriptlets estarem registados (máx. 1 s):
                // sem isto o documento podia nascer antes do registo e o anti-bloqueador ganhava a corrida.
                var ready = onNavigate(browser, url);
                if (ready.IsCompleted) return CefReturnValue.Continue;
                Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(1))).ContinueWith(_ =>
                {
                    try { using (callback) callback.Continue(true); }
                    catch (Exception) { }
                }, TaskScheduler.Default);
                return CefReturnValue.ContinueAsync;
            }

            // Só pedidos da rede passam pelas listas (eteris://, devtools://, data:, blob: seguem logo).
            if (!IsNetwork(url)) return CefReturnValue.Continue;

            var pageUrl = SafeFrame.MainUrl(browser);
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && !isProtected(page.Host))
                return CefReturnValue.Continue;

            // Modo rigoroso neste site: scripts e frames de outros domínios não carregam (além das listas).
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var top) && Data.AppData.IsStrict(top.Host)
                && StrictPolicy.ShouldBlock(pageUrl, url, request.ResourceType))
            {
                blocking = true;
                onBlocked(Uri.TryCreate(url, UriKind.Absolute, out var strictBlocked) ? strictBlocked.Host : url);
                return CefReturnValue.Cancel;
            }

            // As opções $domain e $third-party das listas referem-se ao documento que fez o pedido (pode ser um iframe).
            var documentUrl = SafeFrame.Url(frame) is { Length: > 0 } frameUrl && frameUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? frameUrl
                : pageUrl;
            if (!blocker().ShouldBlock(url, documentUrl, ResourceTypes.ToAdblock(request.ResourceType)))
                return CefReturnValue.Continue;

            blocking = true;
            onBlocked(Uri.TryCreate(url, UriKind.Absolute, out var blocked) ? blocked.Host : url);
            return CefReturnValue.Cancel;
        }
        catch (Exception)
        {
            // Frame destacado, separador a fechar…: o pedido segue (ou fica bloqueado, se já se tinha decidido).
            return blocking ? CefReturnValue.Cancel : CefReturnValue.Continue;
        }
    }

    internal static bool IsNetwork(string url)
        => url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("ws:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("wss:", StringComparison.OrdinalIgnoreCase);
}
