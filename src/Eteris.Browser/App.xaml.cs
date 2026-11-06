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
        _ = Shields.PrivacyPreferences.ApplyAsync();
        Data.Settings.Changed += _ => { var __ = Shields.PrivacyPreferences.ApplyAsync(); };
        _ = Shields.ShieldEngine.StartAsync(Shields.FilterLists.ForApp());
        Data.AppData.StartShieldStats();
        new MainWindow(StartupArgs.InitialUrl(e.Args), StartupArgs.IsPrivate(e.Args)).Show();
        _ = UpdateService.CheckAndDownloadAsync();
        _channel = InstanceChannel.Listen(Program.ChannelName, args => Dispatcher.InvokeAsync(() => Eteris.Browser.MainWindow.OpenForwarded(args)));
    }

    private IDisposable? _channel;

    protected override void OnExit(ExitEventArgs e)
    {
        _channel?.Dispose();
        Data.AppData.FlushShieldStats();
        UpdateService.ApplyPendingOnExit();
        Cef.Shutdown();
        base.OnExit(e);
    }
}
