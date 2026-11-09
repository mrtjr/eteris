using System.Windows;
using System.Windows.Media.Animation;
using CefSharp;
using Eteris.Browser.Tabs;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// Centro de multimédia: um botão na barra aparece quando alguma aba tem som; o painel mostra o que toca
// em todas as abas, com tocar/pausar, silenciar e ir para a aba — sem sair da página atual.
public partial class MainWindow
{
    private List<BrowserTab> _mediaTabs = [];

    // ---------- O ponto «a tocar» (E1 do plano de desempenho da 0.2.14) ----------
    // Pulsa (opacidade 1 → 0,35 e de volta) só enquanto se vê: com a janela minimizada ou o ponto escondido o relógio da
    // animação para, e o WPF deixa de enviar um lote ao compositor a cada fotograma. A 30 fotogramas por segundo chega
    // para um pulso de 0,9 s. Com o ajuste desligado (--eteris-ajustes=-e1) volta a ser como até à 0.2.13: eterna desde
    // o Loaded.
    private bool _liveDotPulsing;

    private void SetUpLiveDot()
    {
        // Braço «puro» das medições (ETERIS_TEST_PURO=animacoes; nunca numa instalação): sem animação nenhuma.
        if (PerfDiagnostics.Without(PerfLayers.Animations)) return;
        if (!PerfTuning.IsOn(PerfTuning.WaitingDot))
        {
            MediaLiveDot.Loaded += (_, _) => MediaLiveDot.BeginAnimation(OpacityProperty, LiveDotPulse(limitFrames: false));
            return;
        }
        MediaLiveDot.IsVisibleChanged += (_, _) => PulseLiveDot();
        StateChanged += (_, _) => PulseLiveDot();
    }

    private void PulseLiveDot()
    {
        if (!PerfTuning.IsOn(PerfTuning.WaitingDot) || PerfDiagnostics.Without(PerfLayers.Animations)) return;
        var run = MediaLiveDot.IsVisible && WindowState != WindowState.Minimized;
        if (run == _liveDotPulsing) return;
        _liveDotPulsing = run;
        // null tira a animação (e o relógio dela); a opacidade volta ao valor de base.
        MediaLiveDot.BeginAnimation(OpacityProperty, run ? LiveDotPulse(limitFrames: true) : null);
    }

    private static DoubleAnimation LiveDotPulse(bool limitFrames)
    {
        var pulse = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        if (limitFrames) Timeline.SetDesiredFrameRate(pulse, 30);
        return pulse;
    }

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
            var result = await PageScript.EvaluateAsync(browser, MediaInfo.ToggleScript, userGesture: true);
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
                PageScript.Run(browser, MediaInfo.PauseScript);
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
