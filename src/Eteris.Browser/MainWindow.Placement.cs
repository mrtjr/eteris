using System.IO;
using System.Windows;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// A primeira janela normal reabre com o tamanho, a posição e o estado (maximizada) da última vez.
public partial class MainWindow
{
    private static string PlacementPath => Path.Combine(AppPaths.Root, "window.json");

    private void RestorePlacement()
    {
        if (!_ownsSession) return;
        Closing += (_, _) => SavePlacement();
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (WindowPlacement.Load(PlacementPath)?.FitTo(screen) is not { } placement) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var maximized = WindowState == WindowState.Maximized && !_pageFullscreen
                        || (_pageFullscreen && _beforeFullscreenState == WindowState.Maximized);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty) return;
        WindowPlacement.Save(PlacementPath, new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized));
    }
}
