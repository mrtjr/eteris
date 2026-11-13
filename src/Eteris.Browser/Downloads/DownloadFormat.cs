using System.Globalization;
using System.IO;
using System.Text;

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

    // Tira os caracteres de controlo e de formato (U+202E inverte a leitura: «Fatura» + U+202E + «fdp.exe» lia-se «Faturaexe.pdf»).
    internal static string StripInvisible(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.Format)) sb.Append(c);
        return sb.ToString();
    }

    // O nome em duas partes para a linha da transferência: só a cabeça pode ser cortada com reticências; a cauda (a
    // extensão e mais 6 caracteres) fica sempre inteira. Parte-se o texto em vez de contar caracteres para as
    // reticências porque a largura depende das letras (34 «W» não cabem na coluna).
    public static (string Head, string Tail) SplitName(string name)
    {
        name = StripInvisible(name);
        var extension = Path.GetExtension(name).Length;
        var tailLength = (extension is 0 or > 12 ? 2 : extension) + 6;
        return name.Length <= tailLength ? ("", name) : (name[..^tailLength], name[^tailLength..]);
    }

    public static string Progress(long received, long total, long speed)
    {
        var text = total > 0 ? $"{Size(received)} de {Size(total)}" : Size(received);
        return speed > 0 ? $"{text} · {Size(speed)}/s" : text;
    }
}
