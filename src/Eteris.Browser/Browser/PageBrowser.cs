using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.Wpf.HwndHost;

namespace Eteris.Browser.Browser;

// A página de uma aba: o ChromiumWebBrowser do CefSharp (página em janela própria) com duas diferenças na janela que o
// aloja (a «static» do CefSharp):
// - não tem desenho próprio (PageWindow.PaintStep);
// - nasce por baixo das outras páginas e a janela do CEF nasce já no tamanho final (BirthSize).
public class PageBrowser : ChromiumWebBrowser
{
    public PageBrowser(string address) : base(address)
    {
    }

    // Tamanho, em píxeis, da área onde a página vai aparecer. Sem ele, a janela do CEF nasce com o tamanho da static
    // no momento em que o WPF a cria — 0×0 numa aba nova, ainda por dispor — e o Chromium começa a página a 0×0 e
    // volta a dispô-la logo a seguir no tamanho real (mais uma mudança de tamanho antes da 1.ª pintura).
    public Func<(int Width, int Height)>? BirthSize { get; init; }

    // Chamado pelo CefSharp dentro de BuildWindowCore, logo depois de criar a static e antes de pedir ao CEF a janela.
    protected override IWindowInfo CreateBrowserWindowInfo(IntPtr handle)
    {
        var info = base.CreateBrowserWindowInfo(handle);
        Infrastructure.StartupTrace.Mark("aba.hwnd");
        // Uma janela nova fica por cima das irmãs: a de uma aba nova taparia a página à vista assim que aparecesse. Por
        // baixo de todas, é a troca de abas (MainWindow.PageVisibility.cs) que a sobe quando ela já tiver pintado.
        SetWindowPos(handle, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        var (width, height) = PageWindow.BirthSize((info.Width, info.Height), BirthSize?.Invoke());
        info.Width = width;
        info.Height = height;
        return info;
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

    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr handle, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
