using CefSharp;

namespace Eteris.Browser.Shields;

public sealed class PrivacyRequestHandler(IRequestBlocker blocker, Func<string, bool> isProtected, Action onNavigate, Action<string> onBlocked)
    : CefSharp.Handler.RequestHandler
{
    private readonly PrivacyResourceHandler _resources = new(blocker, isProtected, onNavigate, onBlocked);

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => _resources;

    private sealed class PrivacyResourceHandler(IRequestBlocker blocker, Func<string, bool> isProtected, Action onNavigate, Action<string> onBlocked)
        : CefSharp.Handler.ResourceRequestHandler
    {
        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, IRequestCallback callback)
        {
            request.SetHeaderByName("DNT", "1", true);
            request.SetHeaderByName("Sec-GPC", "1", true);

            if (request.ResourceType == ResourceType.MainFrame)
            {
                onNavigate();
                return CefReturnValue.Continue;
            }

            var pageUrl = browser.MainFrame?.Url;
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && !isProtected(page.Host))
                return CefReturnValue.Continue;

            if (!blocker.ShouldBlock(request.Url, pageUrl)) return CefReturnValue.Continue;
            onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var blocked) ? blocked.Host : request.Url);
            return CefReturnValue.Cancel;
        }
    }
}
