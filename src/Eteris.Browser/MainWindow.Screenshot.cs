using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CefSharp;
using CefSharp.DevTools;
using CefSharp.DevTools.Page;
using CefSharp.Wpf;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

// Captura da página (área visível ou página inteira): guarda em Imagens › Éteris e copia para a área de transferência.
// Em OSR o Chromium só pinta o tamanho do widget, por isso a página inteira é capturada ecrã a ecrã e cosida.
public partial class MainWindow
{
    private const int MaxChunks = 80;

    // Depois do primeiro ecrã, cabeçalhos fixos/pegajosos escondem-se para não aparecerem repetidos.
    private const string HideFixedScript = """
        (() => {
          window.__eterisHidden = [...document.querySelectorAll('body *')].filter(e => {
            const p = getComputedStyle(e).position; return p === 'fixed' || p === 'sticky';
          });
          window.__eterisHidden.forEach(e => { e.dataset.eterisVis = e.style.visibility; e.style.visibility = 'hidden'; });
        })()
        """;

    private const string RestoreFixedScript = """
        (() => {
          (window.__eterisHidden || []).forEach(e => { e.style.visibility = e.dataset.eterisVis || ''; delete e.dataset.eterisVis; });
          delete window.__eterisHidden;
        })()
        """;

    private bool _capturing;

    private async void CapturePage(bool fullPage)
    {
        if (_capturing || ActiveBrowser is not { } browser || Tabs.Active is not { } tab) return;
        _capturing = true;
        try
        {
            using var client = browser.GetDevToolsClient();
            var png = fullPage
                ? await CaptureFullPageAsync(browser, client)
                : (await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Png)).Data;

            Directory.CreateDirectory(ScreenshotNames.Folder);
            var path = Path.Combine(ScreenshotNames.Folder, ScreenshotNames.For(tab.Address, DateTime.Now));
            await File.WriteAllBytesAsync(path, png);
            try { Clipboard.SetImage(Decode(png)); } catch (Exception) { }
            ShowToast(fullPage ? "Página inteira capturada e copiada — guardada em Imagens › Éteris"
                               : "Captura copiada — guardada em Imagens › Éteris");
        }
        catch (Exception)
        {
            ShowToast("Não foi possível capturar esta página");
        }
        finally
        {
            _capturing = false;
        }
    }

    private static async Task<byte[]> CaptureFullPageAsync(ChromiumWebBrowser browser, DevToolsClient client)
    {
        var frame = browser.GetMainFrame();
        async Task<double> Eval(string script)
        {
            var response = await frame.EvaluateScriptAsync(script);
            return response.Success && response.Result is not null ? Convert.ToDouble(response.Result) : 0;
        }

        var total = await Eval("Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0)");
        var view = Math.Max(1, await Eval("innerHeight"));
        var start = await Eval("scrollY");
        total = Math.Min(Math.Max(total, view), ScreenshotNames.MaxSide);

        var chunks = new List<(double Top, BitmapSource Image)>();
        try
        {
            for (var y = 0d; chunks.Count < MaxChunks; y += view)
            {
                var top = await Eval($"scrollTo({{ top: {y}, behavior: 'instant' }}); scrollY");
                await Task.Delay(160);
                var shot = await client.Page.CaptureScreenshotAsync(CaptureScreenshotFormat.Png);
                chunks.Add((top, Decode(shot.Data)));
                if (chunks.Count == 1) await frame.EvaluateScriptAsync(HideFixedScript);
                if (top + view >= total - 1) break;
            }
        }
        finally
        {
            await frame.EvaluateScriptAsync(RestoreFixedScript);
            await frame.EvaluateScriptAsync($"scrollTo({{ top: {start}, behavior: 'instant' }})");
        }

        var first = chunks[0].Image;
        var scale = first.PixelHeight / view;
        var width = first.PixelWidth;
        var height = (int)Math.Min(Math.Round(total * scale), ScreenshotNames.MaxSide);
        var canvas = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var stride = width * 4;
        foreach (var (topCss, image) in chunks)
        {
            var top = (int)Math.Round(topCss * scale);
            var rows = Math.Min(image.PixelHeight, height - top);
            if (rows <= 0 || image.PixelWidth < width) continue;
            var pixels = new byte[stride * rows];
            new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, width, rows), pixels, stride, 0);
            canvas.WritePixels(new Int32Rect(0, top, width, rows), pixels, stride, 0);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canvas));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }
}
