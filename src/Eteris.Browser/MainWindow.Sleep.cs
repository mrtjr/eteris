using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private BrowserTab? _previousActive;
    private DispatcherTimer? _sleepTimer;

    private void ReleaseBrowser(BrowserTab tab)
    {
        ReleaseChromium(tab);
        ReleaseVideoView(tab);
    }

    private void ReleaseChromium(BrowserTab tab)
    {
        if (_scriptlets.Remove(tab, out var injector)) injector.Dispose();
        if (!_browsers.Remove(tab, out var browser)) return;
        BrowserHost.Children.Remove(browser);
        PageScript.Release(browser);
        browser.Dispose();
    }

    // Chamado antes de mostrar a nova aba ativa: acorda-a se estava a dormir e marca a anterior.
    private void OnActiveTabChanging()
    {
        var now = DateTime.UtcNow;
        if (_previousActive is not null) _previousActive.LastActiveUtc = now;
        _previousActive = Tabs.Active;
        if (Tabs.Active is { IsSleeping: true } sleeping)
        {
            sleeping.IsSleeping = false;
            CreateBrowser(sleeping);
        }

        _sleepTimer ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => SleepIdleTabs(), Dispatcher);
        _sleepTimer.Start();
    }

    private bool IsTabOnScreen(BrowserTab tab)
        => ReferenceEquals(tab, Tabs.Active) || _split?.PaneOf(tab) is not null;

    private void SleepIdleTabs()
    {
        var now = DateTime.UtcNow;
        var threshold = TimeSpan.FromMinutes(Data.Settings.Current.SleepMinutes);
        foreach (var tab in Tabs.Tabs.ToList())
        {
            if (!SleepPolicy.ShouldSleep(IsTabOnScreen(tab), tab.IsSleeping, tab.IsLoading, tab.IsAudible,
                    tab.LastActiveUtc, now, threshold)) continue;
            ReleaseBrowser(tab);
            tab.IsSleeping = true;
        }
    }

    private void SleepAllBackgroundTabs()
    {
        foreach (var tab in Tabs.Tabs.Where(t => !IsTabOnScreen(t) && !t.IsSleeping && !t.IsAudible).ToList())
        {
            ReleaseBrowser(tab);
            tab.IsSleeping = true;
        }
    }
}
