using System.Windows;
using CefSharp;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CefConfigurator.Initialize();
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cef.Shutdown();
        base.OnExit(e);
    }
}
