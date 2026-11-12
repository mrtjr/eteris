using System.Windows;
using CefSharp;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Updates;

namespace Eteris.Browser;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupTrace.Mark("onstartup");
        // O motor arranca numa thread própria enquanto o resto do arranque e o XAML da janela correm aqui; a janela espera
        // por ele só antes da 1.ª aba (Infrastructure/CefConfigurator.cs).
        // Com o ajuste e11 já arrancou a partir do Main (Program.cs).
        if (!CefConfigurator.StartedFromMain) CefConfigurator.InitializeInBackground(CefConfigurator.Prepare(), OnCefReady);
        // As tabelas que a 1.ª aba consulta abrem-se em paralelo (Data/AppData.cs, PrewarmAsync).
        _ = Data.AppData.PrewarmAsync();
        // Ajuste e12 (desligado por omissão; regra R-2): a 1.ª nova aba do perfil prepara-se já, em fundo
        // (Browser/NewTabPrecompute.cs). Nunca num arranque privado.
        if (PerfTuning.IsOn(PerfTuning.ReadyNewTab) && !StartupArgs.IsPrivate(e.Args)) Browser.NewTabPrecompute.Start(() => DateTime.Now);
        Theme.ThemeManager.Apply(Data.Settings.Current.Theme);
        Theme.ThemeManager.FollowSystemChanges(() => Data.Settings.Current.Theme);
        Data.Settings.Changed += s => Dispatcher.InvokeAsync(() => Theme.ThemeManager.Apply(s.Theme));
        StartupTrace.Mark("tema");
        // Cor do Éteris: aplicada já, e de novo quando a escolha, a paisagem, o tema ou a cor do Windows mudam.
        Theme.AccentManager.WallpaperSampler = () => Theme.WallpaperAccent.Sample(Data.Settings.Current.NewTabWallpaper);
        Theme.AccentManager.Apply(Data.Settings.Current.Accent, Theme.ThemeManager.Current == Theme.AppTheme.Light);
        StartupTrace.Mark("cor", "accent=" + Data.Settings.Current.Accent + " paisagem=" + Data.Settings.Current.NewTabWallpaper);
        Theme.AccentManager.FollowWindowsChanges(() => Data.Settings.Current.Accent, () => Theme.ThemeManager.Current == Theme.AppTheme.Light);
        Data.Settings.Changed += s => Dispatcher.InvokeAsync(() => Theme.AccentManager.Apply(s.Accent, Theme.ThemeManager.Current == Theme.AppTheme.Light));
        Theme.ThemeManager.Changed += t => Dispatcher.InvokeAsync(() => Theme.AccentManager.Apply(Data.Settings.Current.Accent, t == Theme.AppTheme.Light));
        StartupTrace.Mark("cor.seguir");
        Data.Settings.Changed += _ => { var __ = Shields.PrivacyPreferences.ApplyAsync(); };
        _ = Shields.ShieldEngine.StartAsync(Shields.FilterLists.ForApp());
        StartupTrace.Mark("escudo.lancado");
        CaptureShield.Install();
        Data.AppData.StartShieldStats();
        StartupTrace.Mark("captura");
        var window = new MainWindow(StartupArgs.InitialUrl(e.Args), StartupArgs.IsPrivate(e.Args));
        StartupTrace.Mark("janela.criada");
        window.Show();
        StartupTrace.Mark("janela.show");
        // Depois da 1.ª página (Infrastructure/FirstPaint.cs): nenhuma das duas é precisa para ela.
        FirstPaint.App.SignalAfter(FirstPaint.Fallback);
        FirstPaint.App.Then(UpdateService.StartSchedule);
        FirstPaint.App.Then(CompositionWarmup.Stop);
        // Sincronização: nada aqui sai para a rede. Só com ela ligada, a primeira ronda fica para 30 s depois.
        FirstPaint.App.Then(Sync.SyncService.AfterFirstPaint);
        // O perfil do JIT só grava o arranque (Infrastructure/StartupJit.cs): para pouco depois da 1.ª página.
        FirstPaint.App.Then(() => _ = Task.Delay(StartupJit.RecordAfterFirstPaint).ContinueWith(_ => StartupJit.Stop(), TaskScheduler.Default));
        FirstPaint.App.Then(() =>
        {
            JumpListTasks.Install();
            StartupTrace.Mark("jumplist");
        });
        // O canal já escuta desde o Program.Main; aqui só se ligam os lançamentos guardados e os seguintes à janela.
        Program.Inbox.Attach(args => Dispatcher.InvokeAsync(() => Eteris.Browser.MainWindow.OpenForwarded(args)));
        StartupTrace.Mark("onstartup.fim");
    }

    // Logo a seguir ao Cef.Initialize (na thread onde ele correu): o que precisa do contexto global do motor.
    internal static void OnCefReady()
    {
        // Só nos testes ao vivo: cai antes da 1.ª página (rede de segurança do sandbox).
        if (TestSwitches.CrashBeforePaint) TestSwitches.CrashNow();
        // O «servidor» do auto-teste de privacidade (nomes .invalid servidos dentro do Éteris, nada vai à rede).
        Eteris.Browser.Browser.SelfTestSchemeHandlerFactory.Register(Cef.GetGlobalRequestContext());
        StartupTrace.Mark("selftest");
        _ = Shields.PrivacyPreferences.ApplyAsync();
        StartupTrace.Mark("privacidade");
        // O Escudo também nos pedidos sem aba do perfil (service workers, shared workers): Shields/BrowserlessRequests.cs.
        // Um bloqueio conta na aba que mostra o site de onde o pedido partiu.
        Shields.BrowserlessRequests.Blocked += (top, host) =>
            Current?.Dispatcher.InvokeAsync(() => Eteris.Browser.MainWindow.RecordBrowserlessBlock(top, host));
        Shields.BrowserlessRequests.Install();
        StartupTrace.Mark("escudo.contexto");
    }

    // O Windows vai encerrar ou terminar a sessão: grava-se já a sessão completa, antes de o WPF fechar as janelas uma a uma.
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        Eteris.Browser.MainWindow.FreezeSessionForExit();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Program.Channel?.Dispose();
        // Fecho normal: este arranque não caiu (rede de segurança do sandbox, Infrastructure/StartupGuard.cs).
        StartupGuard.Current.Healthy();
        StartupGuard.Current.Flush();
        Data.AppData.FlushShieldStats();
        Data.AppData.CloseHistory();
        if (Data.Settings.Current.ClearOnExit)
        {
            try { Cef.GetGlobalCookieManager().DeleteCookiesAsync(string.Empty, string.Empty).Wait(TimeSpan.FromSeconds(3)); }
            catch (Exception) { }
        }
        UpdateService.ApplyPendingOnExit();
        // Palavras-passe apagadas há menos de 10 s (ainda com «Anular»): apagam-se de vez.
        Vault.VaultRoutes.CommitPendingOnExit();
        // Uma palavra-passe copiada há menos de 60 s sai da área de transferência (se ainda lá estiver).
        try { Vault.SecureClipboard.ClearPendingOnExit(); }
        catch (Exception) { }
        try
        {
            // Algum browser ainda vivo (nenhum, hoje: abas, Glance e painéis libertam-se no Closed das janelas) liberta-se
            // aqui, no fio da interface, que é o da janela dele; o Cef.Shutdown corre noutra thread (a do Cef.Initialize)
            // e um HwndHost libertado fora do seu fio falhava.
            Shields.BrowserlessRequests.Release();
            Cef.PreShutdown();
            CefConfigurator.Shutdown();
            // Caches da GPU no disco (ajuste e3): com o motor fechado, que as tem abertas. Com «Apagar ao fechar», depois de
            // «Apagar dados › cache» e depois de uma janela privada (Infrastructure/ProfileCleanup.cs).
            if (ProfileCleanup.ShouldRemoveGpuCaches(Data.Settings.Current.ClearOnExit)) ProfileCleanup.RemoveGpuCaches(AppPaths.Profile);
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
