using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
//
// A página que sai não é minimizada logo: fica «quente» (tamanho inteiro, tapada) durante um período de graça curto,
// para que voltar a ela seja só subir a sua janela (Tabs/WarmTabs.cs). Quem está por cima de quem é a ordem das janelas
// das páginas (StackPages): à vista, depois as que saem, depois as que entram frias, depois as quentes.
public partial class MainWindow
{
    // Páginas que já saíram mas continuam à vista, por cima, até a que entrou apresentar (com a ordem por que saíram:
    // a que saiu primeiro é a que está por cima).
    private readonly Dictionary<FrameworkElement, long> _leavingPages = new();
    private long _leavingSerial;

    // Páginas que entraram frias e ainda se estão a desenhar, por baixo das que saem.
    private readonly HashSet<FrameworkElement> _arrivingPages = new();

    // Páginas quentes: deixadas há pouco, tapadas, sem minimizar (Tabs/WarmTabs.cs).
    private readonly Dictionary<ChromiumWebBrowser, WarmPage> _warmPages = new();
    private DispatcherTimer? _warmTimer;

    // Todas as páginas da última disposição (para ordenar as janelas fora de ShowPages).
    private IReadOnlyList<FrameworkElement> _pageElements = [];

    // Páginas de abas fechadas que estavam à vista: só se destroem depois de a vizinha aparecer.
    private readonly HashSet<ChromiumWebBrowser> _retiringPages = new();

    // Fim do carregamento do 1.º documento de cada página (uma aba nova só se mostra depois dele).
    private readonly ConditionalWeakTable<ChromiumWebBrowser, TaskCompletionSource> _firstLoads = new();

    private sealed class WarmPage(DateTime leftUtc)
    {
        public DateTime LeftUtc { get; } = leftUtc;
        public bool PlayingVideo { get; set; }
    }

    // Chamado por LayoutBrowsers com todas as páginas (abas, motor de vídeo, abas fechadas a sair) e se se devem ver.
    private void ShowPages(IReadOnlyList<(FrameworkElement Element, bool Visible)> pages)
    {
        _pageElements = pages.Select(p => p.Element).ToList();
        var plan = PageSwap.Plan(pages.Select(p => (p.Element.Visibility == Visibility.Visible,
            p.Element is ChromiumWebBrowser page && _warmPages.ContainsKey(page), p.Visible, p.Element is ChromiumWebBrowser)).ToList());
        var holding = plan.Contains(PageSwap.Step.Hold);
        var nothingShown = !plan.Contains(PageSwap.Step.Show);
        var showing = new HashSet<FrameworkElement>();
        var arriving = new List<ChromiumWebBrowser>();
        var held = new List<FrameworkElement>();
        var leaving = new List<FrameworkElement>();
        var hidden = new List<FrameworkElement>();
        for (var i = 0; i < pages.Count; i++)
        {
            var element = pages[i].Element;
            switch (plan[i])
            {
                case PageSwap.Step.Show:
                    showing.Add(element);
                    _leavingPages.Remove(element);
                    if (element is ChromiumWebBrowser warm) _warmPages.Remove(warm);
                    if (element.Visibility != Visibility.Visible && element is ChromiumWebBrowser cold)
                    {
                        arriving.Add(cold);
                        // Por baixo da que sai até apresentar; sem nenhuma a sair, fica logo por cima.
                        if (holding) _arrivingPages.Add(cold);
                    }
                    break;
                case PageSwap.Step.Hold:
                    _arrivingPages.Remove(element);
                    _leavingPages[element] = ++_leavingSerial;
                    held.Add(element);
                    break;
                // Ainda à espera de uma troca anterior: é essa que a esconde. A página de uma aba fechada, com nenhuma outra
                // à vista, espera pela vizinha (ao fechar, a aba sai antes de a seguinte ficar ativa).
                case PageSwap.Step.Hide when !_leavingPages.ContainsKey(element) && !(nothingShown && IsRetiringOnScreen(element)):
                    _arrivingPages.Remove(element);
                    (element.Visibility == Visibility.Visible ? leaving : hidden).Add(element);
                    break;
            }
        }
        // Aba fria (minimizada): restaurada já no tamanho final, num só passo, ainda com a static escondida; quando o
        // CefSharp lhe der o tamanho (ao ficar visível no WPF) já é esse.
        foreach (var cold in arriving)
            if (cold.IsBrowserInitialized) ShowPageWindow(cold, ArrivalPixels(cold));
        // Sai sem esperar por ninguém (a que entra já está desenhada: quente, ou o motor de vídeo): quente ou escondida.
        // Quente só por baixo de outra página à vista; sem nenhuma, ver-se-ia no lugar da ativa.
        var cooling = leaving.Where(element => !TryKeepWarm(element, covered: showing.Count > 0)).ToList();
        // A ordem das janelas antes de as mostrar: a que entra fria nunca aparece por cima da que sai.
        StackPages(showing);
        foreach (var element in pages.Select(p => p.Element).Where(showing.Contains))
        {
            element.Visibility = Visibility.Visible;
            if (element is ChromiumWebBrowser cef && cef.IsBrowserInitialized) SetPageVisible(cef, true);
        }
        foreach (var element in cooling.Concat(hidden)) HidePage(element);
        if (held.Count > 0) _ = HideWhenShownAsync(arriving, held);
        ReviewWarmPages();
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
        // Minimizar primeiro: esconder no WPF faz o CefSharp encolher a janela do CEF a 0×0 (ResizeBrowser(0, 0)) e,
        // com a página ainda «visible», o Chromium dispunha-a e apresentava-a a 0×0 antes de a minimizar.
        if (element is ChromiumWebBrowser cef && !cef.IsDisposed && cef.IsBrowserInitialized) SetPageVisible(cef, false);
        element.Visibility = Visibility.Hidden;
    }

    private void DisposePage(ChromiumWebBrowser browser)
    {
        _warmPages.Remove(browser);
        _arrivingPages.Remove(browser);
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
        foreach (var page in arriving) _arrivingPages.Remove(page);
        // As que saíram ficam quentes por baixo da que entrou, ou escondem-se — só depois de esta subir. Se a que entrou
        // já não está à vista (fechou entretanto), nada as tapa: escondem-se.
        var covered = arriving.Any(page => !page.IsDisposed && page.Visibility == Visibility.Visible);
        var cooling = new List<FrameworkElement>();
        foreach (var element in held)
            if (_leavingPages.Remove(element) && !TryKeepWarm(element, covered)) cooling.Add(element);
        StackPages();
        foreach (var element in cooling) HidePage(element);
        ReviewWarmPages();
    }

    // ---------- Abas quentes (Tabs/WarmTabs.cs) ----------

    // Uma página que sai fica quente se puder; a verificação de vídeo a tocar corre a seguir e arrefece-a se for o caso.
    // «covered»: há uma página à vista por cima dela.
    private bool TryKeepWarm(FrameworkElement element, bool covered)
    {
        if (!covered || element is not ChromiumWebBrowser page || !CanStayWarm(page)) return false;
        _warmPages[page] = new WarmPage(DateTime.UtcNow);
        _ = CheckWarmVideoAsync(page);
        return true;
    }

    // Condições para uma página continuar a desenhar tapada (o resto da regra, puro, em WarmTabs.Keep).
    private bool CanStayWarm(ChromiumWebBrowser page)
    {
        if (page.IsDisposed || !page.IsBrowserInitialized || page.Visibility != Visibility.Visible || _retiringPages.Contains(page)) return false;
        // No ecrã dividido as metades não tapam a área toda: uma página quente ver-se-ia no separador.
        if (_split is not null || WindowState == WindowState.Minimized || !IsVisible) return false;
        var tab = _browsers.FirstOrDefault(p => ReferenceEquals(p.Value, page)).Key;
        // Com som (vídeo ou música, mesmo de um iframe de outro site) ou a passar para o motor de vídeo: arrefece já.
        return tab is not null && !tab.IsAudible && tab.Media != MediaState.Playing && !_videoViews.ContainsKey(tab);
    }

    private async Task CheckWarmVideoAsync(ChromiumWebBrowser page)
    {
        var result = await PageScript.EvaluateAsync(page, WarmTabs.PlayingVideoScript, WarmTabs.VideoCheckLimit);
        if (!_warmPages.TryGetValue(page, out var warm)) return;
        warm.PlayingVideo = WarmTabs.PlaysVideo(result.Success, result.Result);
        if (warm.PlayingVideo) ReviewWarmPages();
    }

    // Arrefece (minimiza) as que já não ficam quentes e marca a próxima revisão. Chamado depois de cada disposição, no fim
    // da graça, quando a janela muda de estado e quando uma aba quente começa a tocar som.
    private void ReviewWarmPages()
    {
        if (_warmPages.Count == 0)
        {
            _warmTimer?.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        var candidates = _warmPages
            .Select(p => new WarmTabs.Candidate<ChromiumWebBrowser>(p.Key, p.Value.LeftUtc, CanStayWarm(p.Key), p.Value.PlayingVideo))
            .ToList();
        var keep = WarmTabs.Keep(candidates, now);
        foreach (var page in _warmPages.Keys.Except(keep).ToList())
        {
            _warmPages.Remove(page);
            // Tapada: minimizá-la não se vê (e quem entretanto voltou à vista já saiu da lista em ShowPages).
            if (!page.IsDisposed) HidePage(page);
        }
        if (_warmTimer is null)
        {
            _warmTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _warmTimer.Tick += OnWarmTimer;
        }
        _warmTimer.Stop();
        if (WarmTabs.NextReview(candidates.Where(c => keep.Contains(c.Page)), now) is not { } next) return;
        _warmTimer.Interval = next < MinimumWarmReview ? MinimumWarmReview : next;
        _warmTimer.Start();
    }

    private static readonly TimeSpan MinimumWarmReview = TimeSpan.FromMilliseconds(50);

    private void OnWarmTimer(object? sender, EventArgs e)
    {
        _warmTimer?.Stop();
        ReviewWarmPages();
    }

    private void ForgetWarmPages()
    {
        _warmTimer?.Stop();
        _warmPages.Clear();
    }

    // ---------- Ordem das janelas das páginas ----------

    // Põe as janelas das páginas pela ordem de PageSwap.StackOrder (à vista, a sair, a entrar fria, quentes), de uma só
    // vez e só se não estiverem já assim. «showing»: as que vão ficar visíveis agora (ainda escondidas no WPF).
    private void StackPages(IReadOnlySet<FrameworkElement>? showing = null)
    {
        var stack = new List<(System.Windows.Interop.HwndHost Page, PageSwap.Layer Layer, long Rank)>();
        foreach (var element in _pageElements)
        {
            if (element is not System.Windows.Interop.HwndHost host || host.Handle == IntPtr.Zero || element is ChromiumWebBrowser { IsDisposed: true }) continue;
            if (element.Visibility != Visibility.Visible && showing?.Contains(element) != true) continue;
            if (element is ChromiumWebBrowser page && _warmPages.TryGetValue(page, out var warm)) stack.Add((host, PageSwap.Layer.Warm, -warm.LeftUtc.Ticks));
            else if (_leavingPages.TryGetValue(element, out var serial)) stack.Add((host, PageSwap.Layer.Held, serial));
            else if (_arrivingPages.Contains(element)) stack.Add((host, PageSwap.Layer.Arriving, 0));
            else stack.Add((host, PageSwap.Layer.Shown, 0));
        }
        if (stack.Count < 2) return;
        var wanted = PageSwap.StackOrder(stack).Select(host => host.Handle).ToList();
        if (CurrentStack(wanted).SequenceEqual(wanted)) return;
        // Cada uma logo abaixo da anterior; a de cima não se mexe em relação às janelas que não são páginas.
        var batch = BeginDeferWindowPos(wanted.Count - 1);
        for (var i = 1; i < wanted.Count && batch != IntPtr.Zero; i++)
            batch = DeferWindowPos(batch, wanted[i], wanted[i - 1], 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        if (batch != IntPtr.Zero && EndDeferWindowPos(batch)) return;
        for (var i = 1; i < wanted.Count; i++)
            SetWindowPos(wanted[i], wanted[i - 1], 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    // As mesmas janelas pela ordem em que estão agora (de cima para baixo).
    private static List<IntPtr> CurrentStack(IReadOnlyCollection<IntPtr> windows)
    {
        var order = new List<IntPtr>(windows.Count);
        var parent = GetParent(windows.First());
        for (var child = GetWindow(parent, GwChild); child != IntPtr.Zero && order.Count < windows.Count; child = GetWindow(child, GwHwndNext))
            if (windows.Contains(child)) order.Add(child);
        return order;
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

    // Tamanho final, em píxeis, de uma página que vai aparecer: o que o CefSharp lhe vai dar (PageWindow.PixelSize). A
    // posição na grelha pode ter mudado agora mesmo (ecrã dividido): dispor primeiro.
    private static (int Width, int Height) ArrivalPixels(ChromiumWebBrowser browser)
    {
        if (!browser.IsArrangeValid) browser.UpdateLayout();
        return PageWindow.PixelSize(browser.ActualWidth, browser.ActualHeight, DeviceScale(browser));
    }

    // Também para os motores que não são abas (os sites fixados na barra lateral).
    private static void SetPageVisible(ChromiumWebBrowser browser, bool visible)
    {
        if (!visible)
        {
            if (PageWidget(browser, out _) is { } widget && !IsIconic(widget)) ShowWindow(widget, SwShowMinNoActive);
            return;
        }
        // Com o tamanho que o CefSharp já deu à janela do CEF (a página ficou visível no WPF antes desta chamada).
        if (PageWidget(browser, out var frame) is null) return;
        var size = GetClientRect(frame, out var area) ? (area.Right - area.Left, area.Bottom - area.Top) : (0, 0);
        ShowPageWindow(browser, size);
    }

    // Mostra a janela do Chromium de uma aba escondida (minimizada) já no tamanho dado (PageWindow.RestoreStep).
    private static void ShowPageWindow(ChromiumWebBrowser browser, (int Width, int Height) size)
    {
        if (PageWidget(browser, out _) is not { } widget) return;
        switch (PageWindow.RestoreStep(IsIconic(widget), size))
        {
            case PageWindow.Restore.Placement:
                var placement = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() };
                if (GetWindowPlacement(widget, ref placement))
                {
                    placement.Flags = 0;
                    placement.ShowCmd = SwShowNoActivate;
                    // Numa janela filha, em coordenadas da janela do CEF (a página ocupa-a toda).
                    placement.NormalPosition = new NativeRect { Left = 0, Top = 0, Right = size.Width, Bottom = size.Height };
                    if (SetWindowPlacement(widget, ref placement)) return;
                }
                ShowWindow(widget, SwShowNoActivate);
                return;
            case PageWindow.Restore.ShowOnly:
                ShowWindow(widget, SwShowNoActivate);
                return;
        }
    }

    // Dentro da janela do CEF (CefBrowserWindow) está a do Chromium (Chrome_WidgetWin_*), que é a que conta.
    private static IntPtr? PageWidget(ChromiumWebBrowser browser, out IntPtr frame)
    {
        frame = IntPtr.Zero;
        if (browser.IsDisposed || !browser.IsBrowserInitialized || browser.GetBrowserHost() is not { } host) return null;
        frame = host.GetWindowHandle();
        var widget = frame == IntPtr.Zero ? IntPtr.Zero : GetWindow(frame, GwChild);
        return widget == IntPtr.Zero ? null : widget;
    }

    private const int SwShowNoActivate = 4;
    private const int SwShowMinNoActive = 7;
    private const uint GwChild = 5;
    private const uint GwHwndNext = 2;
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
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public int Length, Flags, ShowCmd;
        public NativePoint MinPosition, MaxPosition;
        public NativeRect NormalPosition;
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
    private static extern bool GetWindowPlacement(IntPtr handle, ref NativePlacement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr handle, ref NativePlacement placement);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginDeferWindowPos(int count);

    [DllImport("user32.dll")]
    private static extern IntPtr DeferWindowPos(IntPtr batch, IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EndDeferWindowPos(IntPtr batch);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
