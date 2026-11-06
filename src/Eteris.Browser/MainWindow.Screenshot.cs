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
            // Em OSR o captureBeyondViewport repete a área visível: para a página inteira, a janela de
            // visualização é emulada com a altura total do conteúdo, captura-se e repõe-se.
            CaptureScreenshotResponse shot;
            if (fullPage)
            {
                var metrics = await client.Page.GetLayoutMetricsAsync();
                var (width, height) = ScreenshotNames.ClampSize(
                    (double)metrics.CssLayoutViewport.ClientWidth, Math.Ceiling(metrics.CssContentSize.Height));
                await client.Emulation.SetDeviceMetricsOverrideAsync((int)width, (int)height, 1, false);
                try
                {
                    await Task.Delay(400);
                    shot = await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Png, null, null, true, false);
                }
                finally
                {
                    await client.Emulation.ClearDeviceMetricsOverrideAsync();
                }
            }
            else
            {
                shot = await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Png);
            }

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
