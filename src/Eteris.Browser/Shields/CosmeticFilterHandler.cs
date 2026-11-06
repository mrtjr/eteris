using CefSharp;

namespace Eteris.Browser.Shields;

// Regras específicas do site: injetadas assim que o JavaScript da página arranca, antes de os anúncios aparecerem.
public sealed class CosmeticFilterHandler(Func<string, bool> isProtected) : IRenderProcessMessageHandler
{
    public void OnContextCreated(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
    {
        if (!frame.IsMain || ShieldEngine.Cosmetic is not { } engine) return;
        if (!Uri.TryCreate(frame.Url, UriKind.Absolute, out var page) || !page.Scheme.StartsWith("http", StringComparison.Ordinal)) return;
        if (!isProtected(page.Host)) return;

        var css = engine.UrlCss(frame.Url);
        if (css.Length > 0) frame.ExecuteJavaScriptAsync(CosmeticScripts.Inject(css));
    }

    public void OnContextReleased(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }

    public void OnFocusedNodeChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IDomNode node) { }

    public void OnUncaughtException(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, JavascriptException exception) { }
}
