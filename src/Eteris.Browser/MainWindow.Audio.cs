using System.Windows;
using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Tabs;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

public partial class MainWindow
{
    private DispatcherTimer? _audioTimer;
    // Última sondagem de cada aba e o empurrão de um carregamento ou título novo (ajuste e4, Tabs/MediaPolling.cs).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BrowserTab, MediaPolling.Poll> _mediaPolls = new();

    private void StartAudioWatch()
    {
        // Braço «puro» das medições de desempenho (só com ETERIS_TEST_PURO; nunca numa instalação).
        if (PerfDiagnostics.Without(PerfLayers.Media)) return;
        _audioTimer = new DispatcherTimer(MediaPolling.Round, DispatcherPriority.Background, async (_, _) =>
        {
            var selective = PerfTuning.IsOn(PerfTuning.MediaPolling);
            foreach (var (tab, browser) in _browsers.ToList())
            {
                if (!browser.IsBrowserInitialized || browser.IsDisposed) continue;
                if (selective)
                {
                    var poll = _mediaPolls.GetOrCreateValue(tab);
                    var now = WarmClock();
                    if (!MediaPolling.Due(MediaSeen(tab, browser), tab.Media, tab.IsAudible, poll.Last, poll.Nudged, now)) continue;
                    poll.Polled(now);
                }
                try
                {
                    PerfCounters.Add("midia.sondagem");
                    var result = await PageScript.EvaluateAsync(browser, MediaInfo.Script);
                    tab.ApplyMedia(result.Success ? MediaInfo.Parse(result.Result as string) : MediaInfo.Nothing);
                }
                catch (Exception) { tab.ApplyMedia(MediaInfo.Nothing); }
            }
            RefreshMediaHub();
        }, Dispatcher);
        _audioTimer.Start();
        Closed += (_, _) => _audioTimer.Stop();
    }

    // A aba vê-se (ou acabou de se ver): a ativa, uma metade do ecrã dividido, uma quente (Tabs/WarmTabs.cs).
    private bool MediaSeen(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser)
        => ReferenceEquals(tab, Tabs.Active) || _split?.PaneOf(tab) is not null || _warmPages.ContainsKey(browser);

    // Um carregamento acabado ou um título novo: a aba entra na ronda seguinte, mesmo escondida (ajuste e4).
    private void NudgeMediaPoll(BrowserTab tab) => _mediaPolls.GetOrCreateValue(tab).Nudge();

    private void ToggleMute(BrowserTab tab)
    {
        tab.IsMuted = !tab.IsMuted;
        if (_browsers.TryGetValue(tab, out var browser) && browser.IsBrowserInitialized)
            browser.GetBrowserHost()?.SetAudioMuted(tab.IsMuted || _pause is not null);
        SetVideoMuted(tab);
    }

    private void TabAudio_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) ToggleMute(tab);
        e.Handled = true;
    }
}
