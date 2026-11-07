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

    // Vários links, um bloco por aba (título e endereço), separados por uma linha em branco.
    public static string TitlesAndAddresses(IEnumerable<(string Title, string Url)> links)
        => string.Join("\n\n", links.Select(l => TitleAndAddress(l.Title, l.Url)));

    // Lista HTML com os títulos como texto dos links (para colar em editores e e-mail).
    public static string HtmlList(IEnumerable<(string Title, string Url)> links)
        => "<ul>" + string.Concat(links.Select(l =>
        {
            var text = OneLine(l.Title);
            return $"<li><a href=\"{Escape(l.Url)}\">{Escape(text.Length == 0 ? l.Url : text)}</a></li>";
        })) + "</ul>";

    // O formato CF_HTML da área de transferência do Windows: cabeçalho com posições (em bytes UTF-8) do HTML e do
    // fragmento. Sem isto, colar em editores HTML só dá texto.
    public static string ClipboardHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        var headerLength = string.Format(header, 0, 0, 0, 0).Length;
        var utf8 = System.Text.Encoding.UTF8;
        var startHtml = headerLength;
        var startFragment = startHtml + utf8.GetByteCount(before);
        var endFragment = startFragment + utf8.GetByteCount(fragment);
        var endHtml = endFragment + utf8.GetByteCount(after);
        return string.Format(header, startHtml, endHtml, startFragment, endFragment) + before + fragment + after;
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string OneLine(string text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
