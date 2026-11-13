using System.Windows;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// Links entregues por um segundo lançamento do Éteris (ex.: clicados noutra app).
public partial class MainWindow
{
    private DateTime _lastActivatedUtc = DateTime.UtcNow;
    // Quando o Éteris, vindo de outra aplicação, voltou a ter o foco (qualquer das suas janelas; MOT-05).
    private DateTime _appActivatedUtc = DateTime.UtcNow;

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
        // Um link de outra aplicação é um gesto do utilizador: acaba a Pausa na janela que o recebe (MainWindow.Pause.cs).
        if (url is not null || StartupArgs.WantsNewTab(args)) target.EndPauseHere();
        if (url is not null) target.Tabs.NewTab(url);
        else if (StartupArgs.WantsNewTab(args)) target.OpenNewTab();
        target.BringToFront();
    }

    private void TrackActivation()
    {
        Activated += (_, _) => _lastActivatedUtc = DateTime.UtcNow;
        // O cartão de permissão vive na janela da camada (MainWindow.Overlay.cs): quem volta de outra aplicação e clica
        // logo nele ativa a camada, não a janela principal. O guarda do cartão recomeça com a ativação do Éteris.
        EventHandler onAppActivated = (_, _) => _appActivatedUtc = DateTime.UtcNow;
        Application.Current.Activated += onAppActivated;
        Closed += (_, _) =>
        {
            if (Application.Current is { } app) app.Activated -= onAppActivated;
        };
    }

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
