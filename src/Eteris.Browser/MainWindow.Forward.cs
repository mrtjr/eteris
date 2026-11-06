using System.Windows;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// Links entregues por um segundo lançamento do Éteris (ex.: clicados noutra app).
public partial class MainWindow
{
    private DateTime _lastActivatedUtc = DateTime.UtcNow;

    internal static void OpenForwarded(string[] args)
    {
        var url = StartupArgs.InitialUrl(args);
        if (StartupArgs.IsPrivate(args))
        {
            OpenWindow(isPrivate: true, url);
            return;
        }

        var target = Application.Current.Windows.OfType<MainWindow>()
            .Where(w => !w.IsPrivate)
            .MaxBy(w => w._lastActivatedUtc);
        if (target is null)
        {
            OpenWindow(isPrivate: false, url);
            return;
        }
        if (url is not null) target.Tabs.NewTab(url);
        target.BringToFront();
    }

    private void TrackActivation() => Activated += (_, _) => _lastActivatedUtc = DateTime.UtcNow;

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Truque clássico para vencer o bloqueio de primeiro plano do Windows sem roubar o foco de forma permanente.
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
