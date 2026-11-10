using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;
using Microsoft.Win32;

namespace Eteris.Browser;

// E8 do plano de desempenho da 0.2.14 (ajuste e8, ligado por omissão: o diagnóstico D4 mostrou que a página continuava
// a desenhar a 60 fotogramas por segundo com a janela minimizada; com o ajuste deixa de desenhar, 6,47% → 0,59% de um
// núcleo). Com a janela minimizada, noutra área de trabalho virtual ou com a sessão bloqueada, a página à vista (e as
// duas metades do ecrã dividido) esconde-se como uma aba de fundo (minimizar a janela do Chromium,
// MainWindow.PageVisibility.cs); quando a janela volta a ver-se, volta pelo caminho da aba fria. A janela tapada por
// outra não entra aqui: continua a desenhar.
//   - minimizar: o StateChanged desta janela corre antes do do CefSharp (que põe todas as páginas a 0×0, leitura 7 do
//     plano): a página fica escondida antes de encolher, a ordem do T5 (docs/motor.md);
//   - área de trabalho: uma janela noutra área de trabalho fica «cloaked» pelo DWM (DWMWA_CLOAKED), o mesmo sinal que o
//     Chromium usa; vê-se de segundo a segundo só enquanto a janela não está ativa;
//   - sessão bloqueada: SystemEvents.SessionSwitch.
// Nunca esconde uma página com som, com multimédia começada, com captura autorizada ou com Picture-in-Picture
// (Tabs/UnseenWindow.cs). Ao restaurar, as páginas voltam depois de o CefSharp lhes devolver o tamanho.
public partial class MainWindow
{
    private readonly List<ChromiumWebBrowser> _unseenPages = [];
    private UnseenWindow.Reason _unseenReason;
    private DispatcherTimer? _cloakTimer;
    private bool _sessionLocked;

    private void WatchUnseen()
    {
        if (!PerfTuning.IsOn(PerfTuning.HideWhenUnseen)) return;
        void OnSession(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)) return;
            Dispatcher.InvokeAsync(() =>
            {
                _sessionLocked = e.Reason == SessionSwitchReason.SessionLock;
                ReviewUnseen();
            });
        }
        SystemEvents.SessionSwitch += OnSession;
        Closed += (_, _) =>
        {
            SystemEvents.SessionSwitch -= OnSession;
            _cloakTimer?.Stop();
        };
        // Outra área de trabalho: só enquanto a janela não está ativa (mudar de área de trabalho desativa-a).
        _cloakTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ReviewUnseen(), Dispatcher);
        _cloakTimer.Stop();
        Deactivated += (_, _) => _cloakTimer.Start();
        Activated += (_, _) =>
        {
            _cloakTimer.Stop();
            ReviewUnseen();
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _cloakTimer.Stop(); };
    }

    // Chamado também pelo OnWindowStateChanged, antes de o CefSharp encolher as páginas.
    private void ReviewUnseen()
    {
        if (!PerfTuning.IsOn(PerfTuning.HideWhenUnseen) || !IsLoaded) return;
        var reason = UnseenWindow.Why(WindowState == WindowState.Minimized, IsCloaked(), _sessionLocked);
        if (reason == _unseenReason) return;
        _unseenReason = reason;
        if (reason == UnseenWindow.Reason.None) RestoreUnseenPages();
        else HideUnseenPages();
    }

    private void HideUnseenPages()
    {
        foreach (var (tab, browser) in _browsers.ToList())
        {
            if (!ShouldShow(tab, browser) || browser.Visibility != Visibility.Visible || _unseenPages.Contains(browser)) continue;
            if (!UnseenWindow.MayHide(tab.IsAudible, tab.Media, IsCapturing(browser), pictureInPicture: false)) continue;
            SetPageVisible(browser, false);
            _unseenPages.Add(browser);
            PerfCounters.Add("janela.escondeu");
            // Picture-in-Picture de documento não se vê daqui: pergunta-se à página e, se tiver, volta já.
            _ = CheckUnseenPictureInPictureAsync(browser);
        }
        // O site fixado à vista na barra lateral (sem som conhecido: só se esconde se não tiver captura).
        if (_sidebarOpen && CurrentWebPanelBrowser is { Visibility: Visibility.Visible } panel && !_unseenPages.Contains(panel) && !IsCapturing(panel))
        {
            SetPageVisible(panel, false);
            _unseenPages.Add(panel);
            _ = CheckUnseenPictureInPictureAsync(panel);
        }
    }

    private async Task CheckUnseenPictureInPictureAsync(ChromiumWebBrowser browser)
    {
        try
        {
            var result = await PageScript.EvaluateAsync(browser, UnseenWindow.PictureInPictureScript, TimeSpan.FromMilliseconds(500));
            if (result.Success && result.Result is "1" && _unseenPages.Remove(browser)) RestoreUnseenPage(browser);
        }
        catch (Exception) { }
    }

    private void RestoreUnseenPages()
    {
        if (_unseenPages.Count == 0) return;
        var pages = _unseenPages.ToList();
        _unseenPages.Clear();
        // Depois de o CefSharp devolver o tamanho às páginas (o StateChanged dele corre depois deste).
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var page in pages) RestoreUnseenPage(page);
        }, DispatcherPriority.Loaded);
    }

    // O caminho da aba fria (ShowPageWindow), só para as que ainda devem estar à vista.
    private void RestoreUnseenPage(ChromiumWebBrowser page)
    {
        if (page.IsDisposed || page.Visibility != Visibility.Visible) return;
        var tab = _browsers.FirstOrDefault(p => ReferenceEquals(p.Value, page)).Key;
        var shown = tab is not null ? ShouldShow(tab, page) : _sidebarOpen && ReferenceEquals(page, CurrentWebPanelBrowser);
        if (!shown) return;
        ShowPageWindow(page, ArrivalPixels(page));
        PerfCounters.Add("janela.voltou");
    }

    private static bool IsCapturing(ChromiumWebBrowser browser)
    {
        try { return browser.IsBrowserInitialized && browser.GetBrowser() is { } core && CaptureSessions.IsCapturing(core.Identifier); }
        catch (Exception) { return true; }
    }

    // A janela está noutra área de trabalho virtual (ou escondida pelo sistema): o DWM tem-na «cloaked».
    private bool IsCloaked()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private const int DwmwaCloaked = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
