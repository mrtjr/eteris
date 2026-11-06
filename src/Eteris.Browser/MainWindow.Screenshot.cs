using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CefSharp;
using CefSharp.DevTools.Page;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

// Captura da página (área visível ou página inteira) via DevTools: guarda em Imagens › Éteris e copia.
public partial class MainWindow
{
    private async void CapturePage(bool fullPage)
    {
        if (ActiveBrowser is not { } browser || Tabs.Active is not { } tab) return;
        try
        {
            using var client = browser.GetDevToolsClient();
            Viewport? clip = null;
            if (fullPage)
            {
                var metrics = await client.Page.GetLayoutMetricsAsync();
                var (width, height) = ScreenshotNames.ClampSize(metrics.CssContentSize.Width, metrics.CssContentSize.Height);
                clip = new Viewport { X = 0, Y = 0, Width = width, Height = height, Scale = 1 };
            }
            var shot = await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Png, null, clip, true, fullPage);

            Directory.CreateDirectory(ScreenshotNames.Folder);
            var path = Path.Combine(ScreenshotNames.Folder, ScreenshotNames.For(tab.Address, DateTime.Now));
            await File.WriteAllBytesAsync(path, shot.Data);

            using (var stream = new MemoryStream(shot.Data))
            {
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                try { Clipboard.SetImage(image); } catch (Exception) { }
            }
            ShowToast(fullPage ? "Página inteira capturada e copiada — guardada em Imagens › Éteris"
                               : "Captura copiada — guardada em Imagens › Éteris");
        }
        catch (Exception)
        {
            ShowToast("Não foi possível capturar esta página");
        }
    }
}
