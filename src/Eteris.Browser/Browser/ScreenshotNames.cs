using System.Globalization;
using System.IO;

namespace Eteris.Browser.Browser;

public static class ScreenshotNames
{
    // O Chromium não captura texturas com mais de 16384 px num dos lados.
    public const double MaxSide = 16384;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Éteris");

    public static string For(string url, DateTime localTime)
    {
        var site = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
            : "eteris";
        var safe = string.Concat(site.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ':'));
        return $"{safe} {localTime.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture)}.png";
    }

    public static (double Width, double Height) ClampSize(double width, double height)
        => (Math.Min(width, MaxSide), Math.Min(height, MaxSide));
}
