using CefSharp;

namespace Eteris.Browser.Browser;

// Pop-ups nunca abrem janelas: o pedido do Chromium é sempre cancelado e entregue à janela, que decide (às vezes
// uns milissegundos depois, à espera de saber onde o utilizador clicou) se abre uma aba do Éteris ou o bloqueia.
public sealed class PopupToTabHandler(Action<Shields.PopupRequest> onPopup) : CefSharp.Handler.LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl,
        string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures,
        IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser? newBrowser)
    {
        newBrowser = null;
        var opener = frame?.Url is { Length: > 0 } frameUrl ? frameUrl : browser.MainFrame?.Url;
        onPopup(new Shields.PopupRequest(targetUrl, opener, userGesture,
            IsPopupWindow: targetDisposition == WindowOpenDisposition.NewPopup,
            Foreground: targetDisposition != WindowOpenDisposition.NewBackgroundTab,
            AtUtc: DateTime.UtcNow));
        return true;
    }
}

public sealed class FaviconHandler(Action<string?> onFavicon, Action<bool>? onFullscreen = null) : CefSharp.Handler.DisplayHandler
{
    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
        => onFavicon(urls.Count > 0 ? urls[0] : null);

    // Ecrã inteiro pedido pela página (vídeos, jogos, apresentações).
    protected override void OnFullscreenModeChange(IWebBrowser chromiumWebBrowser, IBrowser browser, bool fullscreen)
        => onFullscreen?.Invoke(fullscreen);
}
