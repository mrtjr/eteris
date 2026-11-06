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
        new MainWindow(StartupArgs.InitialUrl(e.Args), StartupArgs.IsPrivate(e.Args)).Show();
        _ = UpdateService.CheckAndDownloadAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UpdateService.ApplyPendingOnExit();
        Cef.Shutdown();
        base.OnExit(e);
    }
}
