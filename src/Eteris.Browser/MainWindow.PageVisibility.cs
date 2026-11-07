using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Abas escondidas sem custo, como as abas de fundo do Chrome, Firefox e Brave.
//
// Com a página desenhada numa janela própria, o CEF não tem forma de dizer ao Chromium que uma aba deixou de se ver
// (WasHidden só funciona fora da tela): a página continuava «visible» e a desenhar animações a 60 fps, ~9% de 1
// núcleo por aba animada escondida. O Chromium marca a página como escondida quando a sua janela é minimizada (é a
// reação a minimizar a janela do navegador), e mostra-a ao restaurar. A janela de uma aba escondida não se vê de
// qualquer forma, por isso minimiza-se: a página passa a «hidden», deixa de desenhar e os temporizadores abrandam,
// mas continua a correr (o chat e o correio continuam a receber mensagens).
public partial class MainWindow
{
    // Chamado por LayoutBrowsers sempre que uma aba entra ou sai de vista.
    private static void SetPageVisible(BrowserTab tab, ChromiumWebBrowser browser, bool visible) => SetPageVisible(browser, visible);

    // Também para os motores que não são abas (os sites fixados na barra lateral).
    private static void SetPageVisible(ChromiumWebBrowser browser, bool visible)
    {
        if (browser.IsDisposed || browser.GetBrowserHost() is not { } host) return;
        // Dentro da janela do CEF (CefBrowserWindow) está a do Chromium (Chrome_WidgetWin_*), que é a que conta.
        var frame = host.GetWindowHandle();
        var widget = frame == IntPtr.Zero ? IntPtr.Zero : GetWindow(frame, GwChild);
        if (widget == IntPtr.Zero || IsIconic(widget) == !visible) return;
        ShowWindow(widget, visible ? SwShowNoActivate : SwShowMinNoActive);
        // Restaurar repõe o último tamanho «normal», que pode ser o minúsculo de quando a aba estava escondida:
        // a janela do Chromium volta a ocupar toda a área da aba.
        if (visible && GetClientRect(frame, out var area))
            SetWindowPos(widget, IntPtr.Zero, 0, 0, area.Right - area.Left, area.Bottom - area.Top, SwpNoZOrder | SwpNoActivate);
    }

    private const int SwShowNoActivate = 4;
    private const int SwShowMinNoActive = 7;
    private const uint GwChild = 5;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
