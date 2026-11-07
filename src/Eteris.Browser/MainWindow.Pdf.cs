using System.IO;
using CefSharp;
using Eteris.Browser.Downloads;

namespace Eteris.Browser;

public partial class MainWindow
{
    // Guarda a página atual como PDF na pasta de transferências (com fundos e cores, como no ecrã).
    private async void SaveAsPdf()
    {
        if (ActiveBrowser is not { } browser || Tabs.Active is not { } tab) return;
        try
        {
            var folder = DownloadPaths.DownloadsFolder();
            var path = DownloadPaths.Unique(folder, PdfName.For(tab.Title, tab.Address));
            var ok = await browser.PrintToPdfAsync(path, new PdfPrintSettings { PrintBackground = true });
            if (ok) ShowToast($"PDF guardado em Transferências — {Path.GetFileName(path)}", OpenFileAction(path), ShowInFolderAction(path));
            else ShowToast("Não foi possível guardar o PDF");
        }
        catch (Exception)
        {
            ShowToast("Não foi possível guardar o PDF");
        }
    }
}
