using CefSharp;

namespace Eteris.Browser.Browser;

// Pop-ups nunca abrem janelas: ou viram aba (se a política deixar) ou são bloqueados e contados no escudo.
public sealed class PopupToTabHandler(Func<bool, string, string?, bool> allow, Action<string, bool> openTab, Action<string> onBlocked)
    : CefSharp.Handler.LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl,
        string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures,
        IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser? newBrowser)
    {
        newBrowser = null;
        var opener = frame?.Url is { Length: > 0 } frameUrl ? frameUrl : browser.MainFrame?.Url;
        if (!allow(userGesture, targetUrl, opener))
        {
            onBlocked(Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) ? target.Host : "pop-up");
            return true;
        }

        openTab(targetUrl, targetDisposition != WindowOpenDisposition.NewBackgroundTab);
        return true;
    }
}

public sealed class FaviconHandler(Action<string?> onFavicon) : CefSharp.Handler.DisplayHandler
{
    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
        => onFavicon(urls.Count > 0 ? urls[0] : null);
}
