using System.IO;
using System.Windows.Media.Imaging;
using CefSharp;
using CefSharp.DevTools.Page;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    // Pouco depois de a página carregar (com a aba à vista), guarda uma miniatura JPEG pequena para o cartão da aba.
    private async void CaptureThumbnail(BrowserTab tab, ChromiumWebBrowser browser, string url)
    {
        if (!Thumbnails.ShouldCapture(url)) return;
        try
        {
            await Task.Delay(1200);
            if (browser.IsDisposed || !ReferenceEquals(tab, Tabs.Active) || tab.Address != url || browser.ActualWidth < 50) return;
            using var client = browser.GetDevToolsClient();
            var scale = Thumbnails.ScaleFor(browser.ActualWidth);
            var clip = new Viewport { X = 0, Y = 0, Width = browser.ActualWidth, Height = browser.ActualHeight, Scale = scale };
            var shot = await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Jpeg, 70, clip, true, false);
            using var stream = new MemoryStream(shot.Data);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            tab.Thumbnail = image;
        }
        catch (Exception) { }
    }
}
