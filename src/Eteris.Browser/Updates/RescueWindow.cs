using System.Windows;
using System.Windows.Controls;

namespace Eteris.Browser.Updates;

// A janela de «procurar e instalar uma versão corrigida» (StartupRescue, INF-04). Feita em código, sem XAML nem recursos
// da aplicação: ainda não existe uma Application (corre no Program.Main, antes do motor e da janela principal).
// Ao carregar, corre UpdateService.RescueAsync; com uma versão nova, o Update.exe aplica-a e reabre o Éteris (o processo
// acaba lá dentro); senão fecha-se, diz porquê e o arranque segue normalmente.
public sealed class RescueWindow : Window
{
    private readonly string[] _args;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 18, Margin = new Thickness(0, 12, 0, 12) };

    public RescueWindow(string[] args)
    {
        _args = args;
        Title = "Éteris";
        Width = 420;
        Height = 150;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var cancel = new Button { Content = "Cancelar", Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => _cancel.Cancel();
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "A procurar e a descarregar a versão corrigida…", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_bar);
        panel.Children.Add(cancel);
        Content = panel;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _cancel.Cancel();
    }

    private async Task RunAsync()
    {
        var result = await UpdateService.RescueAsync(_args, value => Dispatcher.InvokeAsync(() => _bar.Value = value), _cancel.Token);
        var message = result switch
        {
            RescueResult.NoUpdate => "Não há uma versão mais recente do Éteris. Vai tentar abrir normalmente.",
            RescueResult.Failed => "Não foi possível descarregar a versão corrigida. O Éteris vai tentar abrir normalmente; também pode instalar a versão mais recente a partir de github.com/mrtjr/eteris/releases.",
            _ => null,
        };
        Close();
        if (message is not null) MessageBox.Show(message, "Éteris", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
