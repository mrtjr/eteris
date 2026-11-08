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
        CefConfigurator.InitializeInBackground(CefConfigurator.Prepare(), OnCefReady);
        // As tabelas que a 1.ª aba consulta abrem-se em paralelo (Data/AppData.cs, PrewarmAsync).
        _ = Data.AppData.PrewarmAsync();
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
        FirstPaint.App.Then(() => _ = UpdateService.CheckAndDownloadAsync());
        FirstPaint.App.Then(CompositionWarmup.Stop);
        // O perfil do JIT só grava o arranque (Infrastructure/StartupJit.cs): para pouco depois da 1.ª página.
        FirstPaint.App.Then(() => _ = Task.Delay(StartupJit.RecordAfterFirstPaint).ContinueWith(_ => StartupJit.Stop(), TaskScheduler.Default));
        FirstPaint.App.Then(() =>
        {
            JumpListTasks.Install();
            StartupTrace.Mark("jumplist");
        });
        _channel = InstanceChannel.Listen(Program.ChannelName, args => Dispatcher.InvokeAsync(() => Eteris.Browser.MainWindow.OpenForwarded(args)));
        StartupTrace.Mark("onstartup.fim");
    }

    private IDisposable? _channel;

    // Logo a seguir ao Cef.Initialize (na thread onde ele correu): o que precisa do contexto global do motor.
    private static void OnCefReady()
    {
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

    protected override void OnExit(ExitEventArgs e)
    {
        _channel?.Dispose();
        Data.AppData.FlushShieldStats();
        Data.AppData.CloseHistory();
        if (Data.Settings.Current.ClearOnExit)
        {
            try { Cef.GetGlobalCookieManager().DeleteCookiesAsync(string.Empty, string.Empty).Wait(TimeSpan.FromSeconds(3)); }
            catch (Exception) { }
        }
        UpdateService.ApplyPendingOnExit();
        try
        {
            // Algum browser ainda vivo (nenhum, hoje: abas, Glance e painéis libertam-se no Closed das janelas) liberta-se
            // aqui, no fio da interface, que é o da janela dele; o Cef.Shutdown corre noutra thread (a do Cef.Initialize)
            // e um HwndHost libertado fora do seu fio falhava.
            Shields.BrowserlessRequests.Release();
            Cef.PreShutdown();
            CefConfigurator.Shutdown();
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
