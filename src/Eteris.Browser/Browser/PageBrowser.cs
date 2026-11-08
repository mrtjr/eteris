using System.Runtime.InteropServices;
using CefSharp.Wpf.HwndHost;

namespace Eteris.Browser.Browser;

// A página de uma aba: o ChromiumWebBrowser do CefSharp (página em janela própria) com a janela que o aloja (a
// «static» do CefSharp) sem desenho próprio — ver PageWindow.PaintStep.
public class PageBrowser : ChromiumWebBrowser
{
    public PageBrowser(string address) : base(address)
    {
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (PageWindow.PaintStep(msg))
        {
            case PageWindow.Paint.SkipErase:
                handled = true;
                return 1;
            case PageWindow.Paint.Validate:
                ValidateRect(hwnd, IntPtr.Zero);
                handled = true;
                return IntPtr.Zero;
            default:
                return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr handle, IntPtr rect);
}
