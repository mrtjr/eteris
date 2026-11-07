using System.IO;
using System.Windows.Media.Imaging;

namespace Eteris.Browser.Tabs;

// Miniaturas para o cartão da aba: ~320 px de largura, só de páginas web.
public static class Thumbnails
{
    public const int TargetWidth = 320;

    public static bool ShouldCapture(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // A captura vem do tamanho da página; a redução para o cartão é feita aqui (no .NET, fora da thread da janela) e não
    // pelo Chromium: pedir-lhe uma escala (clip.scale) muda a emulação do dispositivo da página À VISTA durante a
    // fotografia, e o utilizador via a página ficar em branco e minúscula por 50–200 ms («piscar» depois de carregar).
    public static BitmapSource Decode(byte[] data, int targetWidth = TargetWidth)
    {
        var width = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(data);
        if (width > targetWidth) image.DecodePixelWidth = targetWidth;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
