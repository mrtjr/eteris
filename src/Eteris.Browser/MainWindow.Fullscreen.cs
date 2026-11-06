using System.Windows;
using CefSharp;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Ecrã inteiro pedido por uma página (requestFullscreen): sem barras, sem moldura, por cima da barra de tarefas.
public partial class MainWindow
{
    private bool _pageFullscreen;
    private WindowState _beforeFullscreenState;
    private WindowStyle _beforeFullscreenStyle;
    private ResizeMode _beforeFullscreenResize;

    private void OnPageFullscreen(BrowserTab tab, bool fullscreen)
    {
        if (fullscreen == _pageFullscreen || (fullscreen && !ReferenceEquals(tab, Tabs.Active))) return;
        _pageFullscreen = fullscreen;
        if (fullscreen)
        {
            _beforeFullscreenState = WindowState;
            _beforeFullscreenStyle = WindowStyle;
            _beforeFullscreenResize = ResizeMode;
            CloseSuggestions();
            // Para cobrir a barra de tarefas, a janela tem de ser maximizada já sem moldura.
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            ShowToast("Ecrã inteiro — prima Esc para sair");
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _beforeFullscreenStyle;
            ResizeMode = _beforeFullscreenResize;
            WindowState = _beforeFullscreenState;
        }
        ApplyChromeLayout();
    }

    private void ExitPageFullscreen()
    {
        ActiveBrowser?.GetMainFrame()?.ExecuteJavaScriptAsync("document.fullscreenElement && document.exitFullscreen()");
        if (Tabs.Active is { } tab) OnPageFullscreen(tab, false);
    }
}
