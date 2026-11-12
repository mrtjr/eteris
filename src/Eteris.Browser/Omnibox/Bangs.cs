namespace Eteris.Browser.Omnibox;

public sealed record Bang(string Trigger, SearchEngine Engine, string Home);

// Atalhos «!» à moda do DuckDuckGo, resolvidos localmente (a consulta nunca passa pelo motor padrão).
public static class Bangs
{
    public static readonly IReadOnlyList<Bang> All =
    [
        new("w", new("Wikipédia", "https://pt.wikipedia.org/w/index.php?search={q}"), "https://pt.wikipedia.org/"),
        new("wen", new("Wikipedia (inglês)", "https://en.wikipedia.org/w/index.php?search={q}"), "https://en.wikipedia.org/"),
        new("yt", new("YouTube", "https://www.youtube.com/results?search_query={q}"), "https://www.youtube.com/"),
        new("gh", new("GitHub", "https://github.com/search?q={q}"), "https://github.com/"),
        new("map", new("OpenStreetMap", "https://www.openstreetmap.org/search?query={q}"), "https://www.openstreetmap.org/"),
        new("r", new("Reddit", "https://www.reddit.com/search/?q={q}"), "https://www.reddit.com/"),
        new("so", new("Stack Overflow", "https://stackoverflow.com/search?q={q}"), "https://stackoverflow.com/"),
        new("mdn", new("MDN", "https://developer.mozilla.org/pt-BR/search?q={q}"), "https://developer.mozilla.org/"),
        new("g", SearchEngine.Google, "https://www.google.com/"),
        new("b", SearchEngine.Brave, "https://search.brave.com/"),
        new("ddg", SearchEngine.DuckDuckGo, "https://duckduckgo.com/"),
        new("sp", SearchEngine.Startpage, "https://www.startpage.com/"),
        new("t", new("Tradutor", "https://translate.google.com/?sl=auto&tl=pt&text={q}"), "https://translate.google.com/"),
        new("dic", new("Priberam", "https://dicionario.priberam.org/{q}"), "https://dicionario.priberam.org/"),
        new("img", new("Imagens DuckDuckGo", "https://duckduckgo.com/?ia=images&iax=images&q={q}"), "https://duckduckgo.com/?ia=images"),
    ];

    public static Bang? Find(string token)
        => token.Length > 1 && token[0] == '!'
            ? All.FirstOrDefault(b => b.Trigger.Equals(token[1..], StringComparison.OrdinalIgnoreCase))
            : null;

    // Bang no início («!w Lisboa») ou no fim («Lisboa !w»).
    public static (Bang Bang, string Query)? Parse(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (Find(parts[0]) is { } first) return (first, string.Join(' ', parts[1..]));
        if (parts.Length > 1 && Find(parts[^1]) is { } last) return (last, string.Join(' ', parts[..^1]));
        return null;
    }
}
