using CefSharp;

namespace Eteris.Browser.Browser;

public sealed class PopupToTabHandler(Action<string, bool> openTab) : CefSharp.Handler.LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl,
        string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures,
        IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser? newBrowser)
    {
        newBrowser = null;
        var foreground = targetDisposition != WindowOpenDisposition.NewBackgroundTab;
        openTab(targetUrl, foreground);
        return true;
    }
}

public sealed class FaviconHandler(Action<string?> onFavicon) : CefSharp.Handler.DisplayHandler
{
    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
        => onFavicon(urls.Count > 0 ? urls[0] : null);
}
