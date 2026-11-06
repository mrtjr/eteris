using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// «Código QR desta página»: para abrir a página no telemóvel, sem contas nem sincronização.
public partial class MainWindow
{
    private byte[]? _qrPng;
    private string? _qrUrl;

    private void ShowQrCode()
    {
        if (Tabs.Active is not { } tab || !Browser.ReaderScripts.IsCandidate(tab.Address)) return;
        var url = Shields.UrlCleaner.Clean(tab.Address);
        try { _qrPng = QrCode.Png(url); }
        catch (Exception)
        {
            ShowToast("Este endereço é demasiado longo para um código QR");
            return;
        }
        _qrUrl = url;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(_qrPng);
        image.EndInit();
        image.Freeze();
        QrImage.Source = image;
        QrTitle.Text = tab.Title;
        QrCaption.Text = QrCode.Caption(url);
        QrOverlay.Visibility = Visibility.Visible;
        FocusInOverlay(QrOverlay);
    }

    private void CloseQr() => QrOverlay.Visibility = Visibility.Collapsed;

    private void QrOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, QrOverlay)) CloseQr();
    }

    private void QrOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseQr();
    }

    private void QrClose_Click(object sender, RoutedEventArgs e) => CloseQr();

    private void QrCopy_Click(object sender, RoutedEventArgs e)
    {
        if (QrImage.Source is not BitmapSource image) return;
        try
        {
            Clipboard.SetImage(image);
            ShowToast("Código QR copiado como imagem");
        }
        catch (Exception) { ShowToast("Não foi possível copiar a imagem"); }
    }

    private void QrSave_Click(object sender, RoutedEventArgs e)
    {
        if (_qrPng is null || _qrUrl is null) return;
        try
        {
            var path = Eteris.Browser.Downloads.DownloadPaths.Unique(Eteris.Browser.Downloads.DownloadPaths.DownloadsFolder(), QrCode.FileName(_qrUrl));
            File.WriteAllBytes(path, _qrPng);
            ShowToast($"Guardado em Transferências › {Path.GetFileName(path)}");
        }
        catch (Exception) { ShowToast("Não foi possível guardar a imagem"); }
    }
}
