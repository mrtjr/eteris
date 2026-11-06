using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

public partial class MainWindow
{
    private FindResultHandler NewFindHandler()
        => new((active, total) => Dispatcher.InvokeAsync(() =>
            FindCount.Text = FindFormat.Count(FindInput.Text, active, total)));

    private void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FocusInOverlay(FindInput);
        FindInput.SelectAll();
        if (FindInput.Text.Length > 0) Find(forward: true, findNext: false);
    }

    private void CloseFind()
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        FindBar.Visibility = Visibility.Collapsed;
        if (ActiveBrowser is { IsBrowserInitialized: true } initialized) initialized.GetBrowserHost()?.StopFinding(clearSelection: true);
        ReturnFocusToPage();
    }

    private void Find(bool forward, bool findNext)
    {
        if (ActiveBrowser is not { IsBrowserInitialized: true } browser || browser.GetBrowserHost() is not { } host) return;
        var text = FindInput.Text;
        if (text.Length == 0)
        {
            host.StopFinding(clearSelection: true);
            FindCount.Text = string.Empty;
            return;
        }
        host.Find(text, forward, matchCase: false, findNext);
    }

    private void FindInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindPlaceholder.Visibility = FindInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Find(forward: true, findNext: false);
    }

    private void FindInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Find(forward: !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), findNext: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e) => Find(forward: false, findNext: true);

    private void FindNext_Click(object sender, RoutedEventArgs e) => Find(forward: true, findNext: true);

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();
}
