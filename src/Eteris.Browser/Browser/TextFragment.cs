namespace Eteris.Browser.Browser;

// «Copiar link para este destaque»: um endereço com #:~:text=… que, aberto em qualquer navegador Chromium
// (e no Safari/Firefox recentes), desce até ao texto e realça-o. Seleções longas usam início,fim.
public static class TextFragment
{
    public const int WordsAtEachEnd = 4;

    public static string? Link(string pageUrl, string selection)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return null;
        var words = selection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;

        var clean = Shields.UrlCleaner.Clean(pageUrl);
        var hash = clean.IndexOf('#');
        if (hash >= 0) clean = clean[..hash];

        var directive = words.Length <= WordsAtEachEnd * 2 + 1 && string.Join(' ', words).Length <= 80
            ? Encode(string.Join(' ', words))
            : Encode(string.Join(' ', words[..WordsAtEachEnd])) + "," + Encode(string.Join(' ', words[^WordsAtEachEnd..]));
        return clean + "#:~:text=" + directive;
    }

    // Na diretiva, «-», «,» e «&» têm significado: vão sempre codificados.
    private static string Encode(string text)
        => Uri.EscapeDataString(text).Replace("-", "%2D").Replace(",", "%2C").Replace("&", "%26");
}
