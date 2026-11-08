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
        var opener = SafeFrame.Url(frame) is { Length: > 0 } frameUrl ? frameUrl : SafeFrame.MainUrl(browser);
        onPopup(new Shields.PopupRequest(targetUrl, opener, userGesture,
            IsPopupWindow: targetDisposition == WindowOpenDisposition.NewPopup,
            Foreground: targetDisposition != WindowOpenDisposition.NewBackgroundTab,
            AtUtc: DateTime.UtcNow));
        return true;
    }

    // Na thread do CEF: o browser existe (a janela dele já foi criada).
    protected override void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        Infrastructure.StartupTrace.MarkOnce("cef.criado");
        (chromiumWebBrowser as PageBrowser)?.OnCefCreated();
    }
}

// Com a página desenhada numa janela própria, as teclas vão direto para o Chromium. onKeyDown(tecla virtual, Ctrl,
// Shift, Alt) diz se a tecla é um atalho do Éteris; se for, é consumida antes de chegar à página (como no Chrome).
// onKeyUp(tecla virtual) só observa (o «Alt sozinho» decide-se ao largar a tecla): nunca consome.
public sealed class ShortcutKeyboardHandler(Func<int, bool, bool, bool, bool> onKeyDown, Action<int>? onKeyUp = null) : IKeyboardHandler
{
    public bool OnPreKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int windowsKeyCode, int nativeKeyCode,
        CefEventFlags modifiers, bool isSystemKey, ref bool isKeyboardShortcut)
    {
        if (type == KeyType.KeyUp)
        {
            onKeyUp?.Invoke(windowsKeyCode);
            return false;
        }
        return type == KeyType.RawKeyDown && onKeyDown(windowsKeyCode, modifiers.HasFlag(CefEventFlags.ControlDown),
            modifiers.HasFlag(CefEventFlags.ShiftDown), modifiers.HasFlag(CefEventFlags.AltDown));
    }

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

public sealed class FaviconHandler(Action<string?, string?> onFavicon, Action<bool>? onFullscreen = null) : CefSharp.Handler.DisplayHandler
{
    // Maior ícone que vale a pena descodificar: os ICO com muitos tamanhos ficam bem abaixo disto.
    public const int MaxBytes = 1 << 20;

    // Alguns sites anunciam SVG e PNG/ICO. O descodificador nativo só mostra os formatos raster, por isso esses têm
    // prioridade; sem nenhum, segue o primeiro anunciado (a janela descarta-o se não o conseguir descodificar).
    public static string? PreferredFavicon(IList<string> urls)
    {
        var raster = urls.FirstOrDefault(url => Uri.TryCreate(url, UriKind.Absolute, out var icon)
            && (icon.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || icon.AbsolutePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)));
        return raster ?? urls.FirstOrDefault();
    }

    // Resposta do motor que vale a pena descodificar: sucesso, com conteúdo, dentro do limite e não SVG nem HTML
    // (um 404 disfarçado de 200) — o descodificador nativo não os desenha e não vale a pena tentar.
    public static bool AcceptsResponse(int statusCode, string? mimeType, long length)
    {
        if (statusCode is < 200 or > 299 || length <= 0 || length > MaxBytes) return false;
        return mimeType is null
            || (!mimeType.Contains("svg", StringComparison.OrdinalIgnoreCase)
                && !mimeType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
    }

    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
    {
        onFavicon(PreferredFavicon(urls), SafeFrame.MainUrl(browser));
    }

    // Ecrã inteiro pedido pela página (vídeos, jogos, apresentações).
    protected override void OnFullscreenModeChange(IWebBrowser chromiumWebBrowser, IBrowser browser, bool fullscreen)
        => onFullscreen?.Invoke(fullscreen);
}
