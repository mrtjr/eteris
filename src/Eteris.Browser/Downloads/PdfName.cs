using System.IO;

namespace Eteris.Browser.Downloads;

// Nome do PDF a partir do título da página (sem caracteres proibidos) ou, sem título, do domínio.
public static class PdfName
{
    public const int MaxLength = 120;

    public static string For(string title, string url)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Join(' ', new string(title.Select(c => invalid.Contains(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
            clean = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", string.Empty) : "pagina";
        if (clean.Length > MaxLength) clean = clean[..MaxLength].TrimEnd();
        return clean + ".pdf";
    }
}
