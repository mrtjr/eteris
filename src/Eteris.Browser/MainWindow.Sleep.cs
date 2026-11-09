using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
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
        ForgetCnameQuota(browser);
        PageScript.Release(browser);
        // Quente: está tapada pela aba à vista, pode ir já.
        if (_warmPages.ContainsKey(browser))
        {
            DisposePage(browser);
            return;
        }
        // À vista (a aba ativa fechou): fica por cima até a vizinha apresentar, sem um fotograma vazio pelo meio.
        if (browser.Visibility == System.Windows.Visibility.Visible && browser.IsBrowserInitialized && IsVisible)
        {
            RetirePage(browser);
            return;
        }
        DisposePage(browser);
    }

    // A conta das consultas CNAME do browser que sai (Shields/CnameUncloak.cs, CnameQuota).
    private static void ForgetCnameQuota(CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser)
    {
        try
        {
            if (browser.IsBrowserInitialized && browser.GetBrowser() is { } core) Shields.Cname.Quota.Forget(core.Identifier);
        }
        catch (Exception) { }
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
        // Ajuste e13: o sono por pressão de memória, um só para todas as janelas (MainWindow.Pressure.cs).
        WatchMemoryPressure();
    }

    private bool HasEngine(BrowserTab tab) => _browsers.ContainsKey(tab) || _videoViews.ContainsKey(tab);

    private bool IsTabOnScreen(BrowserTab tab)
        => ReferenceEquals(tab, Tabs.Active) || _split?.PaneOf(tab) is not null;

    private void SleepIdleTabs()
    {
        var now = DateTime.UtcNow;
        var threshold = TimeSpan.FromMinutes(Data.Settings.Current.SleepMinutes);
        foreach (var tab in Tabs.Tabs.ToList())
        {
            if (!SleepPolicy.ShouldSleep(IsTabOnScreen(tab), tab.IsSleeping, tab.IsLoading, tab.IsAudible,
                    tab.LastActiveUtc, now, threshold, tab.IsLocked)) continue;
            PutToSleep(tab);
        }
        // As que ficaram acordadas noutro Espaço (com som, ou trancadas) seguem a mesma política: acabado o som,
        // adormecem como as outras; uma trancada fica.
        foreach (var tab in Tabs.AwakeOutOfView())
        {
            if (!SleepPolicy.ShouldSleep(false, false, tab.IsLoading, tab.IsAudible, tab.LastActiveUtc, now, threshold, tab.IsLocked)) continue;
            PutToSleep(tab);
        }
        // Os sites fixados escondidos na barra lateral (ajuste e6, MainWindow.WebPanels.cs).
        SleepIdleWebPanels(now, threshold);
    }


    private void SleepAllBackgroundTabs()
    {
        foreach (var tab in Tabs.Tabs.Where(t => !IsTabOnScreen(t) && !t.IsSleeping && !t.IsAudible && !t.IsLocked).ToList())
            PutToSleep(tab);
    }

    // Adormece a aba: larga o motor. Com o ajuste e13, guarda antes a rolagem da página (até meio segundo de espera) e
    // volta a confirmar, depois da resposta, que a aba continua fora de vista, acordada e sem som; sem o ajuste, o mesmo
    // de sempre (largar já).
    private void PutToSleep(BrowserTab tab)
    {
        if (!PerfTuning.IsOn(PerfTuning.SleepUnderPressure) || !_browsers.TryGetValue(tab, out var browser) || !browser.IsBrowserInitialized)
        {
            FallAsleep(tab);
            return;
        }
        if (!_fallingAsleep.Add(tab)) return;
        _ = SleepKeepingScrollAsync(tab, browser);
    }

    private readonly HashSet<BrowserTab> _fallingAsleep = [];

    private async Task SleepKeepingScrollAsync(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser)
    {
        var address = tab.Address;
        ScriptResult result;
        try { result = await PageScript.EvaluateAsync(browser, SleepScroll.ReadScript, TimeSpan.FromMilliseconds(500)); }
        catch (Exception) { result = ScriptResult.Failed; }
        _fallingAsleep.Remove(tab);
        // Entretanto a aba pode ter fechado, ido para à vista, começado a tocar ou adormecido por outro caminho.
        if (!_browsers.TryGetValue(tab, out var same) || !ReferenceEquals(same, browser) || tab.IsSleeping
            || IsTabOnScreen(tab) || tab.IsAudible || tab.IsLocked) return;
        tab.SleptScroll = result.Success && tab.Address == address ? SleepScroll.From(address, result.Result) : null;
        FallAsleep(tab);
    }

    private void FallAsleep(BrowserTab tab)
    {
        ReleaseBrowser(tab);
        tab.IsSleeping = true;
    }

    // O 1.º carregamento da página principal depois de acordar: a rolagem guardada volta, se for a mesma página.
    private static void RestoreSleptScroll(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string url)
    {
        if (tab.SleptScroll is not { } scroll) return;
        tab.SleptScroll = null;
        if (scroll.Matches(url)) PageScript.Run(browser, scroll.RestoreScript);
    }
}
