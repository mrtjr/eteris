using System.Windows;
using CefSharp;
using Eteris.Browser.Tabs;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

// Ecrã inteiro — pedido por uma página (requestFullscreen) ou escolhido no menu: sem barras, sem moldura, por cima
// da barra de tarefas. Só quem entrou é que sai por si: um vídeo que sai do ecrã inteiro não desfaz o do menu.
public partial class MainWindow
{
    // A janela está em ecrã inteiro, seja por quem for (lido pelas outras partes da janela).
    private bool _pageFullscreen;
    // Foi a página que a pôs assim; false quando foi o menu.
    private bool _fullscreenByPage;
    // A página ativa tem um elemento em ecrã inteiro, esteja a janela como estiver.
    private bool _pageHasFullscreenElement;
    private WindowState _beforeFullscreenState;
    private WindowStyle _beforeFullscreenStyle;
    private ResizeMode _beforeFullscreenResize;

    private void OnPageFullscreen(BrowserTab tab, bool fullscreen)
    {
        if (fullscreen && !ReferenceEquals(tab, Tabs.Active)) return;
        _pageHasFullscreenElement = fullscreen;
        if (fullscreen)
        {
            // Com o ecrã inteiro já escolhido no menu, o vídeo entra e sai sem mexer na janela.
            if (_pageFullscreen) return;
            _fullscreenByPage = true;
            SetWindowFullscreen(true);
        }
        else if (_pageFullscreen && _fullscreenByPage) SetWindowFullscreen(false);
    }

    // Botão «Ecrã inteiro» do menu do Éteris.
    private void ToggleWindowFullscreen()
    {
        if (_pageFullscreen)
        {
            ExitPageFullscreen();
            return;
        }
        _fullscreenByPage = false;
        SetWindowFullscreen(true);
    }

    private void SetWindowFullscreen(bool fullscreen)
    {
        if (fullscreen == _pageFullscreen) return;
        _pageFullscreen = fullscreen;
        if (!fullscreen) _fullscreenByPage = false;
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
            ShowToast(_fullscreenByPage ? "Ecrã inteiro — prima Esc para sair" : "Ecrã inteiro — leve o rato ao topo para as barras; Esc para sair");
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

    // Esc ou F11: sai do ecrã inteiro, venha ele da página ou do menu. O exitFullscreen só vai à página que tem mesmo
    // um elemento em ecrã inteiro (não precisa de gesto do utilizador).
    private void ExitPageFullscreen()
    {
        if (_pageHasFullscreenElement && ActiveBrowser is { } browser)
        {
            PageScript.Run(browser, "document.fullscreenElement && document.exitFullscreen()");
            // Com o ecrã inteiro escolhido no menu, o Esc sai só do vídeo (como no Chrome); o seguinte sai da janela.
            if (!_fullscreenByPage) return;
        }
        SetWindowFullscreen(false);
    }
}
