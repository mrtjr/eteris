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

// Com a página desenhada numa janela própria, as teclas vão direto para o Chromium. onKeyDown(tecla virtual, Ctrl,
// Shift, Alt) diz se a tecla é um atalho do Éteris; se for, é consumida antes de chegar à página (como no Chrome).
public sealed class ShortcutKeyboardHandler(Func<int, bool, bool, bool, bool> onKeyDown) : IKeyboardHandler
{
    public bool OnPreKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int windowsKeyCode, int nativeKeyCode,
        CefEventFlags modifiers, bool isSystemKey, ref bool isKeyboardShortcut)
        => type == KeyType.RawKeyDown && onKeyDown(windowsKeyCode, modifiers.HasFlag(CefEventFlags.ControlDown),
            modifiers.HasFlag(CefEventFlags.ShiftDown), modifiers.HasFlag(CefEventFlags.AltDown));

    public bool OnKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int windowsKeyCode, int nativeKeyCode,
        CefEventFlags modifiers, bool isSystemKey) => false;
}

// A página recebeu o foco do teclado (clique ou Tab).
public sealed class PageFocusHandler(Action onGotFocus) : IFocusHandler
{
    public void OnGotFocus(IWebBrowser chromiumWebBrowser, IBrowser browser) => onGotFocus();

    public bool OnSetFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, CefFocusSource source) => false;

    public void OnTakeFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, bool next) { }
}

public sealed class FaviconHandler(Action<string?> onFavicon, Action<bool>? onFullscreen = null) : CefSharp.Handler.DisplayHandler
{
    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
        => onFavicon(urls.Count > 0 ? urls[0] : null);

    // Ecrã inteiro pedido pela página (vídeos, jogos, apresentações).
    protected override void OnFullscreenModeChange(IWebBrowser chromiumWebBrowser, IBrowser browser, bool fullscreen)
        => onFullscreen?.Invoke(fullscreen);
}
