using System.Windows;
using System.Windows.Input;

namespace Eteris.Browser;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Navigate(AddressBox.Text);
        e.Handled = true;
    }

    private void Navigate(string input)
    {
        var url = input.Trim();
        if (url.Length == 0) return;
        if (!url.Contains("://")) url = "https://" + url;
        Browser.Load(url);
    }

    private void Browser_AddressChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AddressBox.Text = e.NewValue as string ?? string.Empty;

    private void Browser_TitleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => Title = $"{e.NewValue as string} — Éteris";
}
