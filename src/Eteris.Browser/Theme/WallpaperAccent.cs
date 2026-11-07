using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Eteris.Browser.Theme;

// «Da paisagem»: a cor dominante da paisagem da nova aba (as imagens embutidas em Browser/Wallpapers), lida uma
// vez por imagem a partir de uma versão reduzida (32 px de largura): nada sai do computador e custa menos de um
// milissegundo. Null sem paisagem («none») ou se a imagem não existir.
public static class WallpaperAccent
{
    private const int SampleWidth = 32;
    private static readonly Dictionary<string, Color?> Cache = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    public static Color? Sample(string wallpaper)
    {
        if (wallpaper is "none" or "") return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(wallpaper, out var cached)) return cached;
            var color = Decode(wallpaper);
            Cache[wallpaper] = color;
            return color;
        }
    }

    private static Color? Decode(string wallpaper)
    {
        try
        {
            using var stream = typeof(WallpaperAccent).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.Wallpapers." + wallpaper + ".jpg");
            if (stream is null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = SampleWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            var colors = new List<Color>(bgra.PixelWidth * bgra.PixelHeight);
            for (var i = 0; i + 3 < pixels.Length; i += 4) colors.Add(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]));
            return Accent.Dominant(colors);
        }
        catch (Exception) { return null; }
    }
}
