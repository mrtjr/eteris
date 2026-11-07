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
        CefConfigurator.Initialize();
        Theme.ThemeManager.Apply(Data.Settings.Current.Theme);
        Theme.ThemeManager.FollowSystemChanges(() => Data.Settings.Current.Theme);
        Data.Settings.Changed += s => Dispatcher.InvokeAsync(() => Theme.ThemeManager.Apply(s.Theme));
        // Cor do Éteris: aplicada já, e de novo quando a escolha, a paisagem, o tema ou a cor do Windows mudam.
        Theme.AccentManager.WallpaperSampler = () => Theme.WallpaperAccent.Sample(Data.Settings.Current.NewTabWallpaper);
        Theme.AccentManager.Apply(Data.Settings.Current.Accent, Theme.ThemeManager.Current == Theme.AppTheme.Light);
        Theme.AccentManager.FollowWindowsChanges(() => Data.Settings.Current.Accent, () => Theme.ThemeManager.Current == Theme.AppTheme.Light);
        Data.Settings.Changed += s => Dispatcher.InvokeAsync(() => Theme.AccentManager.Apply(s.Accent, Theme.ThemeManager.Current == Theme.AppTheme.Light));
        Theme.ThemeManager.Changed += t => Dispatcher.InvokeAsync(() => Theme.AccentManager.Apply(Data.Settings.Current.Accent, t == Theme.AppTheme.Light));
        _ = Shields.PrivacyPreferences.ApplyAsync();
        Data.Settings.Changed += _ => { var __ = Shields.PrivacyPreferences.ApplyAsync(); };
        _ = Shields.ShieldEngine.StartAsync(Shields.FilterLists.ForApp());
        Data.AppData.StartShieldStats();
        new MainWindow(StartupArgs.InitialUrl(e.Args), StartupArgs.IsPrivate(e.Args)).Show();
        _ = UpdateService.CheckAndDownloadAsync();
        JumpListTasks.Install();
        _channel = InstanceChannel.Listen(Program.ChannelName, args => Dispatcher.InvokeAsync(() => Eteris.Browser.MainWindow.OpenForwarded(args)));
    }

    private IDisposable? _channel;

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
        Cef.Shutdown();
        base.OnExit(e);
    }
}
