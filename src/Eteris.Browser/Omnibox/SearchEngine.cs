namespace Eteris.Browser.Omnibox;

public sealed record SearchEngine(string Name, string Template)
{
    public static readonly SearchEngine DuckDuckGo = new("DuckDuckGo", "https://duckduckgo.com/?q={q}");
    public static readonly SearchEngine Brave = new("Brave Search", "https://search.brave.com/search?q={q}");
    public static readonly SearchEngine Startpage = new("Startpage", "https://www.startpage.com/do/search?q={q}");
    // Uma escolha nas Definições, nunca o padrão nem nas Boas-vindas («Nenhum deles cria um perfil seu»); sem sugestões
    // enquanto escreve (SearchSuggest), como o Startpage.
    public static readonly SearchEngine Google = new("Google", "https://www.google.com/search?q={q}");

    public static SearchEngine Default => DuckDuckGo;

    public static SearchEngine ById(string id) => id switch
    {
        "brave" => Brave,
        "startpage" => Startpage,
        "google" => Google,
        _ => DuckDuckGo,
    };

    // Motor escolhido em eteris://settings.
    public static SearchEngine Preferred => ById(Data.Settings.Current.SearchEngine);

    public string UrlFor(string query) => Template.Replace("{q}", Uri.EscapeDataString(query));
}
