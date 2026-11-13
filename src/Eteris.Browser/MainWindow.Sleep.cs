using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private BrowserTab? _previousActive;
    private DispatcherTimer? _sleepTimer;
    // A página pediu para ficar acordada (texto por enviar, Picture-in-Picture; SleepProbe), até quando.
    private readonly Dictionary<BrowserTab, DateTime> _sleepHolds = [];
    // A ronda anterior do sono por tempo e o regresso da última suspensão do computador (o tempo suspenso não conta).
    private DateTime? _lastSleepRoundUtc;
    private DateTime? _sleepFloorUtc;

    private bool IsHeld(BrowserTab tab, DateTime nowUtc) => _sleepHolds.TryGetValue(tab, out var until) && nowUtc < until;

    private bool IsCapturingTab(BrowserTab tab) => _browsers.TryGetValue(tab, out var browser) && IsCapturing(browser);

    private void ReleaseBrowser(BrowserTab tab)
    {
        ReleaseChromium(tab);
        ReleaseVideoView(tab);
    }

    private void ReleaseChromium(BrowserTab tab)
    {
        // O cartão de permissão que esta aba tinha aberto sai com o motor (fechar, adormecer, trocar de motor).
        DropPermissions(p => ReferenceEquals(p.Tab, tab));
        if (_scriptlets.Remove(tab, out var injector)) injector.Dispose();
        ReleaseVaultChannel(tab);
        _sleepHolds.Remove(tab);
        if (!_browsers.Remove(tab, out var browser)) return;
        _pause?.Plan.Forget(browser);
        _replay.Remove(browser);
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

        if (_sleepTimer is null)
        {
            // O construtor com tratador já o liga. Parado no fecho: um temporizador ligado fica na lista do Dispatcher e
            // segurava a janela fechada até o Éteris sair (e uma troca de aba depois de fechar não o volta a ligar).
            _sleepTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => SleepIdleTabs(), Dispatcher);
            Closed += (_, _) => _sleepTimer.Stop();
        }
        // Ajuste e13: o sono por pressão de memória, um só para todas as janelas (MainWindow.Pressure.cs).
        WatchMemoryPressure();
    }

    private bool HasEngine(BrowserTab tab) => _browsers.ContainsKey(tab) || _videoViews.ContainsKey(tab);

    private bool IsTabOnScreen(BrowserTab tab)
        => ReferenceEquals(tab, Tabs.Active) || _split?.PaneOf(tab) is not null;

    private void SleepIdleTabs()
    {
        var now = DateTime.UtcNow;
        // Uma ronda muito depois da anterior: o computador esteve suspenso; o prazo das abas recomeça agora.
        if (SleepPolicy.WasSuspended(_lastSleepRoundUtc, now)) _sleepFloorUtc = now;
        _lastSleepRoundUtc = now;
        var threshold = TimeSpan.FromMinutes(Data.Settings.Current.SleepMinutes);
        foreach (var tab in Tabs.Tabs.ToList())
        {
            if (!SleepPolicy.ShouldSleep(IsTabOnScreen(tab), tab.IsSleeping, tab.IsLoading, tab.IsAudible,
                    SleepPolicy.Later(tab.LastActiveUtc, _sleepFloorUtc), now, threshold, tab.IsLocked,
                    isCapturing: IsCapturingTab(tab), isPlaying: tab.Media == MediaState.Playing, isHeld: IsHeld(tab, now))) continue;
            PutToSleep(tab);
        }
        // As que ficaram acordadas noutro Espaço (com som, trancadas, a aba que estava em uso…) seguem a mesma
        // política: acabado o som, adormecem como as outras; uma trancada fica.
        foreach (var tab in Tabs.AwakeOutOfView())
        {
            if (!SleepPolicy.ShouldSleep(false, false, tab.IsLoading, tab.IsAudible, SleepPolicy.Later(tab.LastActiveUtc, _sleepFloorUtc), now,
                    threshold, tab.IsLocked, isCapturing: IsCapturingTab(tab), isPlaying: tab.Media == MediaState.Playing,
                    isHeld: IsHeld(tab, now))) continue;
            PutToSleep(tab);
        }
        // Os sites fixados escondidos na barra lateral (ajuste e6, MainWindow.WebPanels.cs).
        SleepIdleWebPanels(now, threshold);
    }


    // «Adormecer abas em segundo plano» (paleta): apanha todas as abas de fundo de uma vez, sem o utilizador as ver uma a
    // uma, por isso poupa o mesmo que o sono por tempo — as que gravam (a isenção dura até a página navegar), as que
    // tocam (também sem som) e as que têm texto por enviar ou Picture-in-Picture.
    private void SleepAllBackgroundTabs()
    {
        foreach (var tab in Tabs.Tabs.Where(t => !IsTabOnScreen(t) && !t.IsSleeping && !t.IsAudible && !t.IsLocked).ToList())
            PutToSleep(tab);
    }

    // Adormece a aba: larga o motor. Antes pergunta à página (até meio segundo de espera) a rolagem — guardada com o
    // ajuste e13 — e se tem texto por enviar ou Picture-in-Picture (SleepProbe): o sono por tempo, por pressão e o
    // comando da paleta deixam-na acordada (e não voltam a perguntar durante SleepProbe.HoldFor); a multi-seleção
    // (byUser: o utilizador escolheu essas abas) adormece na mesma. Depois da resposta volta a confirmar que a aba
    // continua fora de vista, acordada e sem som. Sem motor inicializado, larga já.
    private void PutToSleep(BrowserTab tab, bool byUser = false)
    {
        if (!_browsers.TryGetValue(tab, out var browser) || !browser.IsBrowserInitialized)
        {
            FallAsleep(tab);
            return;
        }
        if (!_fallingAsleep.Add(tab)) return;
        _ = SleepKeepingScrollAsync(tab, browser, byUser);
    }

    private readonly HashSet<BrowserTab> _fallingAsleep = [];

    private async Task SleepKeepingScrollAsync(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, bool byUser)
    {
        var address = tab.Address;
        ScriptResult result;
        try { result = await PageScript.EvaluateAsync(browser, SleepProbe.Script, TimeSpan.FromMilliseconds(500)); }
        catch (Exception) { result = ScriptResult.Failed; }
        _fallingAsleep.Remove(tab);
        // Entretanto a aba pode ter fechado, ido para à vista, começado a tocar ou adormecido por outro caminho.
        if (!_browsers.TryGetValue(tab, out var same) || !ReferenceEquals(same, browser) || tab.IsSleeping
            || IsTabOnScreen(tab) || tab.IsAudible || tab.IsLocked) return;
        if (!byUser && (IsCapturing(browser) || tab.Media == MediaState.Playing)) return;
        // Sem resposta (página sem contexto de script: PDF, página de erro) adormece como sempre: não tem texto a perder.
        var probe = result.Success ? SleepProbe.Parse(result.Result) : null;
        if (!byUser && probe is { KeepsAwake: true })
        {
            _sleepHolds[tab] = DateTime.UtcNow + SleepProbe.HoldFor;
            return;
        }
        tab.SleptScroll = PerfTuning.IsOn(PerfTuning.SleepUnderPressure) && probe is not null && tab.Address == address
            ? SleepScroll.From(address, probe.Y) : null;
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
