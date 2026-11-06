using System.Windows;
using Eteris.Browser.Updates;

namespace Eteris.Browser;

public partial class MainWindow
{
    private void WatchUpdates()
    {
        UpdateService.StateChanged += OnUpdateStateChanged;
        Closed += (_, _) => UpdateService.StateChanged -= OnUpdateStateChanged;
        RefreshUpdatePill();

        // Atalho de demonstração (só versões de desenvolvimento): ETERIS_DEMO_UPDATE=0.2.0
        if (Environment.GetEnvironmentVariable("ETERIS_DEMO_UPDATE") is { Length: > 0 } demo)
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(3000);
                UpdateService.SimulateReadyForDemo(demo);
            });
    }

    private void OnUpdateStateChanged() => Dispatcher.InvokeAsync(RefreshUpdatePill);

    private void RefreshUpdatePill()
    {
        var ready = UpdateService.State == UpdateState.Ready;
        UpdatePill.Content = $"Atualizar para {UpdateService.ReadyVersion}";
        UpdatePill.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
    }

    // Fecha as janelas pela ordem normal (a sessão é guardada) e deixa o Update.exe reabrir a versão nova.
    private void UpdatePill_Click(object sender, RoutedEventArgs e)
    {
        UpdateService.RestartRequested = true;
        // Application.Shutdown não dispara Closing (onde a sessão é guardada): fecha-se cada janela.
        foreach (var window in Application.Current.Windows.Cast<Window>().ToList()) window.Close();
    }
}
