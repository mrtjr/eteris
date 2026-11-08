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

    // Páginas que entraram frias e ainda não apresentaram um fotograma desde então: nunca ficam quentes.
    private readonly PagePresentation<ChromiumWebBrowser> _presentation = new();

    // Páginas quentes: deixadas há pouco, tapadas, sem minimizar (Tabs/WarmTabs.cs).
    private readonly Dictionary<ChromiumWebBrowser, WarmPage> _warmPages = new();
    private DispatcherTimer? _warmTimer;

    // Todas as páginas da última disposição (para ordenar as janelas fora de ShowPages).
    private IReadOnlyList<FrameworkElement> _pageElements = [];

    // Páginas de abas fechadas que estavam à vista: só se destroem depois de a vizinha aparecer.
    private readonly HashSet<ChromiumWebBrowser> _retiringPages = new();

    // Fim do carregamento do 1.º documento de cada página (uma aba nova só se mostra depois dele).
    private readonly ConditionalWeakTable<ChromiumWebBrowser, TaskCompletionSource> _firstLoads = new();

    private sealed class WarmPage(TimeSpan leftAt)
    {
        public TimeSpan LeftAt { get; } = leftAt;
        public bool PlayingVideo { get; set; }
    }

    // Relógio das quentes: só anda para a frente (um acerto da hora do sistema não mexe na graça).
    private static TimeSpan WarmClock() => TimeSpan.FromMilliseconds(Environment.TickCount64);

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
                        _presentation.Arrive(cold);
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
        // As quentes que deixam de poder ficar (ecrã dividido, nada à vista por cima) arrefecem antes de mexer na ordem:
        // ainda estão tapadas, e uma quente nunca chega a ficar à vista no separador do ecrã dividido.
        ReviewWarmPages(showing, cooling);
        // A ordem das janelas antes de as mostrar: a que entra fria nunca aparece por cima da que sai, e a que sai e vai
        // ser escondida já está por baixo da que entra.
        StackPages(showing, cooling);
        foreach (var element in pages.Select(p => p.Element).Where(showing.Contains))
        {
            element.Visibility = Visibility.Visible;
            if (element is ChromiumWebBrowser cef && cef.IsBrowserInitialized) SetPageVisible(cef, true);
        }
        foreach (var element in cooling.Concat(hidden)) HidePage(element);
        if (held.Count > 0) _ = HideWhenShownAsync(arriving, held);
        // Sem nenhuma a segurar, ninguém espera pela que entrou: só se regista quando apresenta (para poder ficar quente).
        else if (arriving.Count > 0) _ = WaitPresentedAsync(arriving);
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
        _presentation.Forget(browser);
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
            // Era ela que tapava as quentes.
            ReviewWarmPages();
        });
    }

    private async Task HideWhenShownAsync(List<ChromiumWebBrowser> arriving, List<FrameworkElement> held)
    {
        await WaitPresentedAsync(arriving);
        foreach (var page in arriving) _arrivingPages.Remove(page);
        // As que saíram ficam quentes por baixo da que entrou, ou escondem-se — só depois de esta subir. Se a que entrou
        // já não está à vista (fechou entretanto), nada as tapa: escondem-se.
        var covered = arriving.Any(page => !page.IsDisposed && page.Visibility == Visibility.Visible);
        var cooling = new List<FrameworkElement>();
        foreach (var element in held)
            if (_leavingPages.Remove(element) && !TryKeepWarm(element, covered)) cooling.Add(element);
        ReviewWarmPages(cooling: cooling);
        StackPages(cooling: cooling);
        foreach (var element in cooling) HidePage(element);
    }

    // Espera (até PageSwap.HoldLimit) que as páginas que entraram frias apresentem e regista as que apresentaram. As que
    // não apresentaram a tempo e continuam à vista ficam a ser vigiadas mais um pouco (até lá não podem ficar quentes).
    private async Task WaitPresentedAsync(List<ChromiumWebBrowser> arriving)
    {
        // Lido já (antes de qualquer espera): o número da chegada que esta espera confirma.
        var arrivals = arriving.Select(page => _presentation.Pending(page)).ToList();
        var clock = Stopwatch.StartNew();
        TimeSpan Left() => PageSwap.HoldLimit - clock.Elapsed;
        var presented = new bool[arriving.Count];
        for (var i = 0; i < arriving.Count; i++) presented[i] = await FramePresentedAsync(arriving[i], Left);
        if (arriving.Count > 0) await Task.Delay(PageSwap.PresentMargin);
        for (var i = 0; i < arriving.Count; i++)
        {
            if (arrivals[i] is not { } arrival) continue;
            if (presented[i]) _presentation.Presented(arriving[i], arrival);
            else if (!arriving[i].IsDisposed && arriving[i].Visibility == Visibility.Visible) _ = ConfirmPresentedAsync(arriving[i], arrival);
        }
    }

    // Uma página lenta (mais do que o teto da troca): regista-se quando acabar por apresentar, se ainda for a mesma chegada.
    private async Task ConfirmPresentedAsync(ChromiumWebBrowser page, long arrival)
    {
        var clock = Stopwatch.StartNew();
        if (!await FramePresentedAsync(page, () => WarmTabs.Grace - clock.Elapsed)) return;
        await Task.Delay(PageSwap.PresentMargin);
        _presentation.Presented(page, arrival);
    }

    // A página desenhou um fotograma do documento certo: o 1.º carregamento acabou (numa aba nova, antes dele só há o
    // documento vazio, branco) e o rAF duplo resolveu. «left»: o tempo que ainda resta.
    private async Task<bool> FramePresentedAsync(ChromiumWebBrowser page, Func<TimeSpan> left)
    {
        while (!page.IsDisposed && !page.IsBrowserInitialized && left() > TimeSpan.Zero) await Task.Delay(16);
        if (page.IsDisposed || !page.IsBrowserInitialized) return false;
        if (_firstLoads.TryGetValue(page, out var loaded) && !loaded.Task.IsCompleted)
        {
            if (left() > TimeSpan.Zero) await Task.WhenAny(loaded.Task, Task.Delay(left()));
            if (!loaded.Task.IsCompleted) return false;
        }
        if (left() <= TimeSpan.Zero) return false;
        var frame = await PageScript.EvaluateAsync(page, PageSwap.NextFrameScript, left(), asyncBody: true);
        return frame.Success && frame.Result is true;
    }

    // ---------- Abas quentes (Tabs/WarmTabs.cs) ----------

    // Uma página que sai fica quente se puder; a verificação de vídeo a tocar corre a seguir e arrefece-a se for o caso.
    // «covered»: há uma página à vista por cima dela.
    private bool TryKeepWarm(FrameworkElement element, bool covered)
    {
        if (!covered || element is not ChromiumWebBrowser page || !CanStayWarm(page)) return false;
        var warm = new WarmPage(WarmClock());
        _warmPages[page] = warm;
        _ = CheckWarmVideoAsync(page, warm);
        return true;
    }

    // Condições para uma página continuar a desenhar tapada (o resto da regra, puro, em WarmTabs.Keep).
    private bool CanStayWarm(ChromiumWebBrowser page)
    {
        if (page.IsDisposed || !page.IsBrowserInitialized || page.Visibility != Visibility.Visible || _retiringPages.Contains(page)) return false;
        // Entrou fria e ainda não apresentou (saiu logo a seguir, num Ctrl+Tab rápido de ida e volta): a janela tem uma
        // superfície em branco ou antiga, e uma quente volta sem esperar por nenhum fotograma.
        if (!_presentation.HasPresented(page)) return false;
        // No ecrã dividido as metades não tapam a área toda: uma página quente ver-se-ia no separador.
        if (_split is not null || WindowState == WindowState.Minimized || !IsVisible) return false;
        var tab = _browsers.FirstOrDefault(p => ReferenceEquals(p.Value, page)).Key;
        // Com som (vídeo ou música, mesmo de um iframe de outro site) ou a passar para o motor de vídeo: arrefece já.
        return tab is not null && !tab.IsAudible && tab.Media != MediaState.Playing && !_videoViews.ContainsKey(tab);
    }

    // «warm»: esta saída da página; se entretanto voltou e saiu outra vez, o resultado já não é dela.
    private async Task CheckWarmVideoAsync(ChromiumWebBrowser page, WarmPage warm)
    {
        var result = await PageScript.EvaluateAsync(page, WarmTabs.PlayingVideoScript, WarmTabs.VideoCheckLimit);
        if (!_warmPages.TryGetValue(page, out var current) || !ReferenceEquals(current, warm)) return;
        warm.PlayingVideo = WarmTabs.PlaysVideo(result.Success, result.Result);
        if (warm.PlayingVideo) ReviewWarmPages();
    }

    // Arrefece (minimiza) as que já não ficam quentes e marca a próxima revisão. Chamado em cada disposição (antes de
    // ordenar as janelas), no fim da graça, quando a janela muda de estado e quando uma aba quente começa a tocar som.
    // «showing»: as que vão ficar à vista agora (ainda escondidas no WPF); «cooling»: as que vão ser escondidas agora.
    private void ReviewWarmPages(IReadOnlySet<FrameworkElement>? showing = null, IReadOnlyCollection<FrameworkElement>? cooling = null)
    {
        if (_warmPages.Count == 0)
        {
            _warmTimer?.Stop();
            return;
        }
        // Sem nenhuma outra página à vista por cima (aba ativa sem página, nenhuma aba ativa de passagem), uma quente
        // ver-se-ia no lugar da ativa: arrefecem todas.
        var covered = _pageElements.Any(element => (element.Visibility == Visibility.Visible || showing?.Contains(element) == true)
            && cooling?.Contains(element) != true
            && !(element is ChromiumWebBrowser page && (page.IsDisposed || _warmPages.ContainsKey(page))));
        var now = WarmClock();
        var candidates = _warmPages
            .Select(p => new WarmTabs.Candidate<ChromiumWebBrowser>(p.Key, p.Value.LeftAt, covered && CanStayWarm(p.Key), p.Value.PlayingVideo))
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

    // Põe as janelas das páginas pela ordem de PageSwap.StackOrder (à vista, a sair, a entrar fria, quentes, a esconder),
    // de uma só vez e só se não estiverem já assim. «showing»: as que vão ficar visíveis agora (ainda escondidas no WPF);
    // «cooling»: as que saem e vão ser escondidas logo a seguir (ainda visíveis no WPF).
    private void StackPages(IReadOnlySet<FrameworkElement>? showing = null, IReadOnlyCollection<FrameworkElement>? cooling = null)
    {
        var stack = new List<(System.Windows.Interop.HwndHost Page, PageSwap.Layer Layer, long Rank)>();
        foreach (var element in _pageElements)
        {
            if (element is not System.Windows.Interop.HwndHost host || host.Handle == IntPtr.Zero || element is ChromiumWebBrowser { IsDisposed: true }) continue;
            if (element.Visibility != Visibility.Visible && showing?.Contains(element) != true) continue;
            // Sem isto ficava na camada «à vista», empatada com a que entra, e podia ficar por cima dela enquanto se esconde.
            if (cooling?.Contains(element) == true) stack.Add((host, PageSwap.Layer.Cooling, 0));
            else if (element is ChromiumWebBrowser page && _warmPages.TryGetValue(page, out var warm)) stack.Add((host, PageSwap.Layer.Warm, -warm.LeftAt.Ticks));
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
