namespace Eteris.Browser.Browser;

// Formas de copiar a página atual para colar noutro sítio.
public static class LinkFormats
{
    // [título](endereço) — com os caracteres que partem a sintaxe escapados.
    public static string Markdown(string title, string url)
    {
        var text = OneLine(title);
        if (text.Length == 0) text = url;
        text = text.Replace("\\", "\\\\").Replace("[", @"\[").Replace("]", @"\]");
        return $"[{text}]({url.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29")})";
    }

    public static string TitleAndAddress(string title, string url)
    {
        var text = OneLine(title);
        return text.Length == 0 || text == url ? url : $"{text}\n{url}";
    }

    private static string OneLine(string text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
