using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
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
//
// Ao trocar de aba, a que entra aparece por baixo da que sai e esta só se esconde depois de a nova ter apresentado um
// fotograma (Browser/PageSwap.cs): sem isso, cada troca mostrava 1–20 fotogramas brancos ou do Mica.
public partial class MainWindow
{
    // Páginas que já saíram mas continuam à vista, por cima, até a que entrou apresentar.
    private readonly HashSet<FrameworkElement> _leavingPages = new();

    // Páginas de abas fechadas que estavam à vista: só se destroem depois de a vizinha aparecer.
    private readonly HashSet<ChromiumWebBrowser> _retiringPages = new();

    // Fim do carregamento do 1.º documento de cada página (uma aba nova só se mostra depois dele).
    private readonly ConditionalWeakTable<ChromiumWebBrowser, TaskCompletionSource> _firstLoads = new();

    // Chamado por LayoutBrowsers com todas as páginas (abas, motor de vídeo, abas fechadas a sair) e se se devem ver.
    private void ShowPages(IReadOnlyList<(FrameworkElement Element, bool Visible)> pages)
    {
        var plan = PageSwap.Plan(pages.Select(p => (p.Element.Visibility == Visibility.Visible, p.Visible, p.Element is ChromiumWebBrowser)).ToList());
        var arriving = new List<ChromiumWebBrowser>();
        for (var i = 0; i < pages.Count; i++)
        {
            if (plan[i] != PageSwap.Step.Show) continue;
            var element = pages[i].Element;
            _leavingPages.Remove(element);
            if (element.Visibility != Visibility.Visible && element is ChromiumWebBrowser arrivingPage)
            {
                // Por baixo das outras janelas de página: a que sai continua a ver-se por cima.
                if (arrivingPage.Handle != IntPtr.Zero) SetWindowPos(arrivingPage.Handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                arriving.Add(arrivingPage);
            }
            element.Visibility = Visibility.Visible;
            if (element is ChromiumWebBrowser cef && cef.IsBrowserInitialized) SetPageVisible(cef, true);
        }
        var held = new List<FrameworkElement>();
        var nothingShown = !plan.Contains(PageSwap.Step.Show);
        for (var i = 0; i < pages.Count; i++)
        {
            var element = pages[i].Element;
            if (plan[i] == PageSwap.Step.Hold)
            {
                _leavingPages.Add(element);
                held.Add(element);
            }
            // Ainda à espera de uma troca anterior: é essa que a esconde. A página de uma aba fechada, com nenhuma outra
            // à vista, espera pela vizinha (ao fechar, a aba sai antes de a seguinte ficar ativa).
            else if (plan[i] == PageSwap.Step.Hide && !_leavingPages.Contains(element) && !(nothingShown && IsRetiringOnScreen(element))) HidePage(element);
        }
        if (held.Count > 0) _ = HideWhenShownAsync(arriving, held);
    }

    private bool IsRetiringOnScreen(FrameworkElement element)
        => element is ChromiumWebBrowser page && _retiringPages.Contains(page) && page.Visibility == Visibility.Visible;

    private void HidePage(FrameworkElement element)
    {
        if (element is ChromiumWebBrowser retired && _retiringPages.Remove(retired))
        {
            DisposePage(retired);
            return;
        }
        element.Visibility = Visibility.Hidden;
        if (element is ChromiumWebBrowser cef && !cef.IsDisposed && cef.IsBrowserInitialized) SetPageVisible(cef, false);
    }

    private void DisposePage(ChromiumWebBrowser browser)
    {
        BrowserHost.Children.Remove(browser);
        if (!browser.IsDisposed) browser.Dispose();
    }

    // A página de uma aba fechada que estava à vista fica (calada) até a vizinha aparecer; depois destrói-se.
    private void RetirePage(ChromiumWebBrowser browser)
    {
        browser.GetBrowserHost()?.SetAudioMuted(true);
        _retiringPages.Add(browser);
        Dispatcher.InvokeAsync(LayoutBrowsers);
        // Recurso: nada apareceu a tempo (era a última aba e a janela fecha, por exemplo).
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(PageSwap.HoldLimit * 2);
            if (!_retiringPages.Remove(browser)) return;
            _leavingPages.Remove(browser);
            DisposePage(browser);
        });
    }

    private async Task HideWhenShownAsync(List<ChromiumWebBrowser> arriving, List<FrameworkElement> held)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan Left() => PageSwap.HoldLimit - clock.Elapsed;
        foreach (var page in arriving)
        {
            while (!page.IsDisposed && !page.IsBrowserInitialized && Left() > TimeSpan.Zero) await Task.Delay(16);
            if (page.IsDisposed || !page.IsBrowserInitialized) continue;
            // Aba nova (ou acordada): antes do fim do 1.º carregamento só há o documento vazio, branco.
            if (_firstLoads.TryGetValue(page, out var loaded) && !loaded.Task.IsCompleted && Left() > TimeSpan.Zero)
                await Task.WhenAny(loaded.Task, Task.Delay(Left()));
            if (Left() > TimeSpan.Zero) await PageScript.EvaluateAsync(page, PageSwap.NextFrameScript, Left(), asyncBody: true);
        }
        if (arriving.Count > 0) await Task.Delay(PageSwap.PresentMargin);
        foreach (var element in held)
            if (_leavingPages.Remove(element)) HidePage(element);
    }

    // O foco do teclado ficava na página da aba que se escondeu (minimizada): o Ctrl+Tab seguinte, e qualquer tecla,
    // iam para uma página invisível e perdiam-se (medido: o 3.º Ctrl+Tab seguido já não trocava). Como no Chrome, a
    // página da aba que entra recebe o foco.
    private void MoveFocusOffHiddenPage()
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (ActiveBrowser is not { } target || !GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero) return;
        foreach (var (tab, page) in _browsers)
        {
            if (IsTabOnScreen(tab) || page.Handle == IntPtr.Zero || !IsChild(page.Handle, info.Focus)) continue;
            Keyboard.Focus(target);
            return;
        }
    }

    // Tamanho, em píxeis, da área das páginas: o de uma aba que se vê sozinha (fora do ecrã dividido), e o de qualquer aba
    // acabada de criar (nasce fora das metades do ecrã dividido).
    private (int Width, int Height) PageAreaPixels()
        => PageWindow.PixelSize(BrowserHost.ActualWidth, BrowserHost.ActualHeight, DeviceScale(BrowserHost));

    // Escala do ecrã como o CefSharp a lê (CompositionTarget.TransformToDevice).
    private static double DeviceScale(Visual visual)
        => PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

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
    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
