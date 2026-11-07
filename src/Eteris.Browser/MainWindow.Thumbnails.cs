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
        if (IsPrivate || !Thumbnails.ShouldCapture(url)) return;
        var version = tab.ThumbnailVersion;
        bool StillCurrent() => !browser.IsDisposed && tab.ThumbnailVersion == version && tab.Address == url
            && _browsers.TryGetValue(tab, out var current) && ReferenceEquals(current, browser);
        try
        {
            await Task.Delay(1200);
            if (!StillCurrent()) return;
            if (!ReferenceEquals(tab, Tabs.Active) || browser.ActualWidth < 50 || browser.ActualHeight < 50)
            {
                // Sem captura nova (aba em segundo plano): a miniatura de outra página do mesmo site enganaria.
                if (tab.ThumbnailAddress != url) tab.Thumbnail = null;
                return;
            }
            using var client = browser.GetDevToolsClient();
            var scale = Thumbnails.ScaleFor(browser.ActualWidth);
            var clip = new Viewport { X = 0, Y = 0, Width = browser.ActualWidth, Height = browser.ActualHeight, Scale = scale };
            var shot = await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Jpeg, 70, clip, true, false);
            if (!StillCurrent()) return;
            using var stream = new MemoryStream(shot.Data);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            tab.Thumbnail = image;
            tab.ThumbnailAddress = url;
        }
        catch (Exception) { }
    }
}
