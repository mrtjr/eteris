using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Eteris.Browser.Infrastructure;

public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TABBEDWINDOW = 4;

    // O material de fundo do sistema só existe a partir do Windows 11 22H2.
    public static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;

    public static void DisableMica(Window window, Brush opaqueBackground)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var none = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.GlassFrameThickness = new Thickness(0);
        window.Background = opaqueBackground;
    }

    // Mica «tabbed» (como o Edge e o Terminal): a moldura deixa ver o ambiente de trabalho desfocado e tingido.
    public static bool TryEnableMica(Window window)
    {
        if (!SupportsBackdrop) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || HwndSource.FromHwnd(hwnd) is not { CompositionTarget: { } target }) return false;

        var backdrop = DWMSBT_TABBEDWINDOW;
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0) return false;

        target.BackgroundColor = Colors.Transparent;
        if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.GlassFrameThickness = new Thickness(-1);
        window.Background = Brushes.Transparent;
        return true;
    }

    public static void Apply(Window window, uint borderColorRef)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var dark = 1;
        var corners = DWMWCP_ROUND;
        var border = (int)borderColorRef;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
