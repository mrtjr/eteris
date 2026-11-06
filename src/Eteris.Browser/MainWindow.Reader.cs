using System.Windows;
using CefSharp;
using System.Windows.Input;
using CefSharp.Wpf;
using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private static async void DetectReaderable(BrowserTab tab, ChromiumWebBrowser browser, string url)
    {
        if (!ReaderScripts.IsCandidate(url)) return;
        try
        {
            var response = await browser.EvaluateScriptAsync(ReaderScripts.Detect, TimeSpan.FromSeconds(3));
            if (response.Success && response.Result is true && tab.Address == url) tab.IsReaderable = true;
        }
        catch (Exception) { }
    }

    private void Reader_Click(object sender, RoutedEventArgs e) => ToggleReader();

    private async void ToggleReader()
    {
        if (ActiveBrowser is not { } browser || Tabs.Active is not { IsReaderable: true }) return;
        try
        {
            var response = await browser.EvaluateScriptAsync(ReaderScripts.Toggle, TimeSpan.FromSeconds(5));
            if (response.Success && response.Result as string == "none") ShowToast("Não foi possível encontrar o artigo nesta página");
            else if (response.Success) Keyboard.Focus(browser);
        }
        catch (Exception) { }
    }
}
