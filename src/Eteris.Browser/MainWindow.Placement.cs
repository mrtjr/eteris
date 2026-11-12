using System.IO;
using System.Windows;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// As janelas normais reabrem com o tamanho, a posição e o estado (maximizada) da última vez: a dona guarda-os em
// window.json, as outras na sessão (session.json, campo Windows).
public partial class MainWindow
{
    private static string PlacementPath => Path.Combine(AppPaths.Root, "window.json");

    // saved = a posição de uma janela reposta da sessão; as outras janelas que não são a dona não mexem na posição.
    private void RestorePlacement(WindowPlacement? saved)
    {
        if (_ownsSession) Closing += (_, _) => SavePlacement();
        var stored = _ownsSession ? WindowPlacement.Load(PlacementPath) : saved;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (stored?.FitTo(screen) is not { } placement) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (PlacementSnapshot() is { } placement) WindowPlacement.Save(PlacementPath, placement);
    }

    // Onde a janela está (null se não houver o que guardar): a dona guarda-a em window.json, as outras na sessão.
    private WindowPlacement? PlacementSnapshot()
    {
        // Fechada em janela flutuante, guarda-se o tamanho de antes.
        if (_miniMode)
            return _beforeMiniBounds.IsEmpty ? null : new WindowPlacement(_beforeMiniBounds.Left, _beforeMiniBounds.Top,
                _beforeMiniBounds.Width, _beforeMiniBounds.Height, _beforeMiniState == WindowState.Maximized);
        var maximized = WindowState == WindowState.Maximized && !_pageFullscreen
                        || (_pageFullscreen && _beforeFullscreenState == WindowState.Maximized);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        return bounds.IsEmpty ? null : new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized);
    }
}
