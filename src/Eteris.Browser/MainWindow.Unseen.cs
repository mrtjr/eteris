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
//     Chromium usa; vê-se por um relógio (UnseenWindow.Watches): de segundo a segundo com a janela à vista e não ativa,
//     quatro vezes por segundo enquanto ela está noutra área, até voltar a ver-se (mesmo já ativa);
//   - sessão bloqueada: SystemEvents.SessionSwitch.
// Nunca esconde uma página com som, com multimédia começada, com captura autorizada ou com Picture-in-Picture
// (Tabs/UnseenWindow.cs). Ao restaurar, as páginas voltam depois de o CefSharp lhes devolver o tamanho.
public partial class MainWindow
{
    private readonly List<ChromiumWebBrowser> _unseenPages = [];
    // Páginas com Picture-in-Picture, que ficam à vista até a janela se voltar a ver.
    private readonly HashSet<ChromiumWebBrowser> _unseenKept = [];
    private UnseenWindow.Reason _unseenReason;
    private DispatcherTimer? _cloakTimer;
    private bool _sessionLocked;

    // Chamado no construtor da janela, onde só deixa a vigia marcada: a subscrição do SessionSwitch (que regista a
    // sessão no Windows) e o relógio da área de trabalho nascem depois da 1.ª página (FirstPaint), fora do arranque. A
    // ordem do minimizar não passa por aqui: o OnWindowStateChanged chama o ReviewUnseen desde o construtor.
    private void WatchUnseen()
    {
        if (!PerfTuning.IsOn(PerfTuning.HideWhenUnseen)) return;
        var closed = false;
        Closed += (_, _) => closed = true;
        FirstPaint.App.Then(() =>
        {
            if (closed) return;
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
            // Outra área de trabalho: um relógio, que anda e pára pela regra de UnseenWindow.Watches. Quem o acerta é o
            // ReviewUnseen (em cada tique, ao ativar e ao mudar de estado) e a desativação; ninguém o pára por conta
            // própria — parado ao ativar, uma janela ativada a partir de outra área (ainda tapada nesse instante) ficava
            // com a página em branco até se trocar de aba.
            _cloakTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = UnseenWindow.WatchEvery(_unseenReason) };
            _cloakTimer.Tick += (_, _) => ReviewUnseen();
            Deactivated += (_, _) => WatchCloak();
            Activated += (_, _) =>
            {
                // Só se ativa uma janela numa sessão desbloqueada: se o aviso do desbloqueio se perder, a página não fica
                // escondida para sempre (com a janela dada por «sem se ver», cada página que entra também se esconde).
                _sessionLocked = false;
                ReviewUnseen();
            };
            // A janela pode já não estar ativa (ou já estar noutra área) quando a 1.ª página aparece.
            ReviewUnseen();
            WatchCloak();
        });
    }

    // Chamado também pelo OnWindowStateChanged, antes de o CefSharp encolher as páginas.
    private void ReviewUnseen()
    {
        if (!PerfTuning.IsOn(PerfTuning.HideWhenUnseen) || !IsLoaded) return;
        var reason = UnseenWindow.Why(WindowState == WindowState.Minimized, IsCloaked(), _sessionLocked);
        if (reason != _unseenReason)
        {
            _unseenReason = reason;
            if (reason == UnseenWindow.Reason.None)
            {
                _unseenKept.Clear();
                RestoreUnseenPages();
            }
            else HideUnseenPages();
        }
        WatchCloak();
    }

    // Liga, desliga e acerta o passo do relógio da área de trabalho (UnseenWindow.Watches e WatchEvery).
    private void WatchCloak()
    {
        if (_cloakTimer is not { } timer) return;
        if (!UnseenWindow.Watches(IsActive, WindowState == WindowState.Minimized, _unseenReason))
        {
            timer.Stop();
            return;
        }
        // Só quando muda: acertar o intervalo ou voltar a ligar um relógio a andar recomeçava a contagem.
        var every = UnseenWindow.WatchEvery(_unseenReason);
        if (timer.Interval != every) timer.Interval = every;
        if (!timer.IsEnabled) timer.Start();
    }

    // A janela continua sem se ver e o que está à vista mudou (uma disposição, uma página que só agora apresentou):
    // esconde-se como o resto. Sem motivo, nada.
    private void KeepUnseenHidden()
    {
        if (_unseenReason != UnseenWindow.Reason.None) HideUnseenPages();
    }

    // Corre quando a janela deixa de se ver e outra vez enquanto ela continuar assim (KeepUnseenHidden): o que já está
    // escondido fica como está.
    private void HideUnseenPages()
    {
        foreach (var (tab, browser) in _browsers.ToList())
        {
            if (!ShouldShow(tab, browser) || browser.Visibility != Visibility.Visible || _unseenKept.Contains(browser)) continue;
            // Entrou e ainda não apresentou um fotograma (a 1.ª página do arranque, uma aba acabada de trocar): escondida,
            // o rAF dela não resolvia e o arranque nunca era dado por bom (StartupGuard.Healthy) — uma queda dessa sessão,
            // mesmo horas depois, contava contra o sandbox como «queda antes da 1.ª página». Esconde-se quando apresentar
            // (KeepUnseenHidden, MainWindow.PageVisibility.cs); a que nunca chegar a apresentar continua a desenhar, como
            // antes do e8.
            if (!_presentation.HasPresented(browser)) continue;
            if (!UnseenWindow.MayHide(tab.IsAudible, tab.Media, IsCapturing(browser), pictureInPicture: false)) continue;
            // Também a que já está na lista: pode ter saído e voltado fria entretanto (restaurada pelo ShowPages).
            SetPageVisible(browser, false);
            if (_unseenPages.Contains(browser)) continue;
            _unseenPages.Add(browser);
            PerfCounters.Add("janela.escondeu");
            // Picture-in-Picture de documento não se vê daqui: pergunta-se à página e, se tiver, volta já.
            _ = CheckUnseenPictureInPictureAsync(browser);
        }
        // O site fixado à vista na barra lateral (sem som conhecido: só se esconde se não tiver captura).
        if (_sidebarOpen && CurrentWebPanelBrowser is { Visibility: Visibility.Visible } panel && !_unseenKept.Contains(panel)
            && !_unseenPages.Contains(panel) && !IsCapturing(panel))
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
            if (result.Success && result.Result is "1" && _unseenPages.Remove(browser))
            {
                // Fica de fora até a janela se voltar a ver: sem isto, cada disposição escondia-a e mostrava-a outra vez.
                _unseenKept.Add(browser);
                RestoreUnseenPage(browser);
            }
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

    // O caminho da aba fria (ShowPageWindow), para todas as que continuam visíveis no WPF: a que está à vista, a que
    // ficou segurada por cima da que entra (um link aberto por outra aplicação com a janela minimizada abre a aba antes
    // de a janela voltar: a página antiga é a que se vê até a nova apresentar) e a que ficou quente (volta sem esperar
    // por fotograma nenhum). Minimizada, qualquer delas era uma janela vazia. As que entretanto se esconderam no WPF
    // ficam como estão, e um site fixado só volta se for o que está à vista na barra lateral.
    private void RestoreUnseenPage(ChromiumWebBrowser page)
    {
        if (page.IsDisposed || page.Visibility != Visibility.Visible) return;
        if (_webPanelBrowsers.ContainsValue(page) && !(_sidebarOpen && ReferenceEquals(page, CurrentWebPanelBrowser))) return;
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
