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

    private void StartAudioWatch()
    {
        // Braço «puro» das medições de desempenho (só com ETERIS_TEST_PURO; nunca numa instalação).
        if (PerfDiagnostics.Without(PerfLayers.Media)) return;
        _audioTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) =>
        {
            foreach (var (tab, browser) in _browsers.ToList())
            {
                if (!browser.IsBrowserInitialized || browser.IsDisposed) continue;
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

    private void ToggleMute(BrowserTab tab)
    {
        tab.IsMuted = !tab.IsMuted;
        if (_browsers.TryGetValue(tab, out var browser) && browser.IsBrowserInitialized)
            browser.GetBrowserHost()?.SetAudioMuted(tab.IsMuted);
        SetVideoMuted(tab);
    }

    private void TabAudio_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) ToggleMute(tab);
        e.Handled = true;
    }
}
