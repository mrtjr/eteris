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
            // SEM clip: com clip (e escala) o Chromium aplica emulação de dispositivo à página à vista enquanto fotografa
            // (docs/motor.md, P1). Sem clip só lê a superfície já desenhada; reduz-se no .NET.
            var shot = await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Jpeg, 70, null, true, false, true);
            if (!StillCurrent()) return;
            var image = await Task.Run(() => Thumbnails.Decode(shot.Data));
            if (!StillCurrent()) return;
            tab.Thumbnail = image;
            tab.ThumbnailAddress = url;
        }
        catch (Exception) { }
    }
}
