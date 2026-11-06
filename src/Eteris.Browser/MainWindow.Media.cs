using System.Windows;
using CefSharp;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Centro de multimédia: um botão na barra aparece quando alguma aba tem som; o painel mostra o que toca
// em todas as abas, com tocar/pausar, silenciar e ir para a aba — sem sair da página atual.
public partial class MainWindow
{
    private List<BrowserTab> _mediaTabs = [];

    private void RefreshMediaHub()
    {
        var tabs = Tabs.Tabs.Where(t => t.Media != MediaState.None).ToList();
        MediaHost.Visibility = tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaLiveDot.Visibility = tabs.Any(t => t.Media == MediaState.Playing && !t.IsMuted) ? Visibility.Visible : Visibility.Collapsed;
        if (!tabs.SequenceEqual(_mediaTabs))
        {
            _mediaTabs = tabs;
            MediaList.ItemsSource = tabs;
        }
        if (tabs.Count == 0) MediaPopup.IsOpen = false;
    }

    private void MediaButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshMediaHub();
        MediaPopup.IsOpen = !MediaPopup.IsOpen;
    }

    private void MediaRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;
        MediaPopup.IsOpen = false;
        Tabs.Active = tab;
    }

    private void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) _ = ToggleMediaAsync(tab);
    }

    private void MediaMute_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            ToggleMute(tab);
            RefreshMediaHub();
        }
    }

    private void PauseAllMedia_Click(object sender, RoutedEventArgs e) => PauseAllMedia();

    private async Task ToggleMediaAsync(BrowserTab tab)
    {
        if (!_browsers.TryGetValue(tab, out var browser) || !browser.IsBrowserInitialized) return;
        try
        {
            var result = await browser.EvaluateScriptAsync(MediaInfo.ToggleScript);
            if (result.Success && result.Result is string state && state.Length > 0)
            {
                // Resposta imediata no painel; a ronda seguinte confirma o estado real.
                tab.Media = state == "playing" ? MediaState.Playing : MediaState.Paused;
                RefreshMediaHub();
            }
        }
        catch (Exception) { }
    }

    private void PauseAllMedia()
    {
        foreach (var tab in Tabs.Tabs.Where(t => t.Media == MediaState.Playing).ToList())
        {
            if (_browsers.TryGetValue(tab, out var browser) && browser.IsBrowserInitialized)
                browser.GetMainFrame()?.ExecuteJavaScriptAsync(MediaInfo.PauseScript);
            tab.Media = MediaState.Paused;
        }
        RefreshMediaHub();
    }

    private void MuteOtherTabs()
    {
        foreach (var tab in Tabs.Tabs.Where(t => !ReferenceEquals(t, Tabs.Active) && !t.IsMuted && t.Media != MediaState.None).ToList())
            ToggleMute(tab);
        RefreshMediaHub();
    }
}
