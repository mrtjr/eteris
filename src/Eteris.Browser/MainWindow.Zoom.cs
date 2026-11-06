using System.Windows;
using System.Windows.Input;
using CefSharp.Wpf;
using Eteris.Browser.Data;
using Eteris.Browser.Palette;

namespace Eteris.Browser;

public partial class MainWindow
{
    // O zoom fica memorizado por site (exceto em janelas privadas, que só o leem).
    private void Zoom(int direction)
    {
        if (ActiveBrowser is not { } browser) return;
        var level = direction == 0
            ? 0
            : ZoomSteps.ToLevel(ZoomSteps.Next(ZoomSteps.ToFactor(browser.ZoomLevel), direction));
        browser.ZoomLevel = level;
        if (!IsPrivate && WebHost(Tabs.Active?.Address) is { } host) AppData.SetSiteZoom(host, level);
        UpdateZoomBadge(level);
    }

    private void ApplySiteZoom(ChromiumWebBrowser browser, string address)
    {
        var level = WebHost(address) is { } host ? AppData.SiteZoom(host) : 0;
        if (Math.Abs(browser.ZoomLevel - level) > 0.001) browser.ZoomLevel = level;
        if (ReferenceEquals(browser, ActiveBrowser)) UpdateZoomBadge(level);
    }

    private void UpdateZoomBadge(double? level = null)
    {
        var current = level ?? ActiveBrowser?.ZoomLevel ?? 0;
        var percent = ZoomSteps.Percent(current);
        ZoomBadge.Visibility = percent == 100 ? Visibility.Collapsed : Visibility.Visible;
        ZoomBadgeText.Text = $"{percent} %";
    }

    private void ZoomBadge_Click(object sender, RoutedEventArgs e)
    {
        Zoom(0);
        ShowToast("Zoom reposto para 100 %");
    }

    // Ctrl + roda do rato: zoom, como em qualquer navegador.
    private void BrowserHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        Zoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }
}
