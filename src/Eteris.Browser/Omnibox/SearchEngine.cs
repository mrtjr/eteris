namespace Eteris.Browser.Omnibox;

public sealed record SearchEngine(string Name, string Template)
{
    public static readonly SearchEngine Atenix = new("Atenix", "https://atenix.duckdns.org/search?q={q}");
    public static readonly SearchEngine DuckDuckGo = new("DuckDuckGo", "https://duckduckgo.com/?q={q}");
    public static readonly SearchEngine Brave = new("Brave Search", "https://search.brave.com/search?q={q}");
    public static readonly SearchEngine Startpage = new("Startpage", "https://www.startpage.com/do/search?q={q}");

    // O Atenix só fica no ar na fase 5; até lá a pesquisa padrão é o DuckDuckGo.
    public static SearchEngine Default => DuckDuckGo;

    public string UrlFor(string query) => Template.Replace("{q}", Uri.EscapeDataString(query));
}
