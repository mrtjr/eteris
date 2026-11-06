using CefSharp;

namespace Eteris.Browser.Shields;

public sealed class PrivacyRequestHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Func<IBrowser, string, Task> onNavigate, Action<string> onBlocked,
    Action? onCrashed = null)
    : CefSharp.Handler.RequestHandler
{
    // O processo da página morreu (crash, falta de memória): a janela mostra a página de erro em vez de um ecrã vazio.
    protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status,
        int errorCode, string errorMessage)
        => onCrashed?.Invoke();

    private readonly PrivacyResourceHandler _resources = new(blocker, isProtected, onNavigate, onBlocked);

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => _resources;

    private sealed class PrivacyResourceHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Func<IBrowser, string, Task> onNavigate, Action<string> onBlocked)
        : CefSharp.Handler.ResourceRequestHandler
    {
        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, IRequestCallback callback)
        {
            request.SetHeaderByName("DNT", "1", true);
            request.SetHeaderByName("Sec-GPC", "1", true);

            if (request.ResourceType == ResourceType.MainFrame)
            {
                if (Data.Settings.Current.HttpsFirst && ShieldEngine.Https.Upgrade(request.Url) is { } secure)
                {
                    request.Url = secure;
                    return CefReturnValue.Continue;
                }
                // O pedido da página só sai depois de os scriptlets estarem registados (máx. 1 s):
                // sem isto o documento podia nascer antes do registo e o anti-bloqueador ganhava a corrida.
                var ready = onNavigate(browser, request.Url);
                if (ready.IsCompleted) return CefReturnValue.Continue;
                Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(1))).ContinueWith(_ =>
                {
                    using (callback) callback.Continue(true);
                }, TaskScheduler.Default);
                return CefReturnValue.ContinueAsync;
            }

            var pageUrl = browser.MainFrame?.Url;
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && !isProtected(page.Host))
                return CefReturnValue.Continue;

            // As opções $domain e $third-party das listas referem-se ao documento que fez o pedido (pode ser um iframe).
            var documentUrl = frame?.Url is { Length: > 0 } frameUrl && frameUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? frameUrl
                : pageUrl;
            if (!blocker().ShouldBlock(request.Url, documentUrl, ResourceTypes.ToAdblock(request.ResourceType)))
                return CefReturnValue.Continue;

            onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var blocked) ? blocked.Host : request.Url);
            return CefReturnValue.Cancel;
        }
    }
}
