using System.Globalization;

namespace Eteris.Browser.Downloads;

public static class DownloadFormat
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0.0", Pt) + " " + units[unit];
    }

    public static string Progress(long received, long total, long speed)
    {
        var text = total > 0 ? $"{Size(received)} de {Size(total)}" : Size(received);
        return speed > 0 ? $"{text} · {Size(speed)}/s" : text;
    }
}
