using System.IO;
using System.Text.Json;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Browser;

public static class NewTabPage
{
    public const string Url = "eteris://newtab/";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(NewTabPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.newtab.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static bool IsNewTab(string address)
        => address.StartsWith("eteris://newtab", StringComparison.OrdinalIgnoreCase);

    public static string Greeting(int hour) => hour switch
    {
        >= 6 and < 12 => "Bom dia",
        >= 12 and < 20 => "Boa tarde",
        _ => "Boa noite",
    };

    // O JSON vai dentro de <script>: o codificador por omissão escapa < > & e impede fechar a tag.
    public const string PrivateUrl = "eteris://newtab/?private";

    public static bool IsPrivate(string url) => url.StartsWith(PrivateUrl, StringComparison.OrdinalIgnoreCase);

    public static string Render(IReadOnlyList<TopSite> sites, SearchEngine engine, DateTime now, int blocked = 0,
        IReadOnlyList<Bookmark>? bookmarks = null, bool isPrivate = false, int[]? week = null)
    {
        // Numa janela privada não se mostra nada do perfil normal.
        if (isPrivate)
        {
            sites = [];
            bookmarks = [];
        }

        var data = new
        {
            @private = isPrivate,
            greeting = Greeting(now.Hour),
            blocked,
            week = isPrivate ? [] : week ?? [],
            weekTotal = isPrivate ? 0 : week?.Sum() ?? 0,
            weekSaved = Shields.PrivacyEstimate.Describe(isPrivate ? 0 : week?.Sum() ?? 0),
            days = Shields.PrivacyEstimate.DayInitials(now),
            engineName = engine.Name,
            engineTemplate = engine.Template,
            bookmarks = (bookmarks ?? []).Take(10).Select(b => new
            {
                url = b.Url,
                title = b.Title.Length > 0 ? b.Title : b.Url,
                letter = SiteBadge.Letter(b.Url) ?? "?",
                color = SiteBadge.Palette[SiteBadge.ColorIndex(b.Url)],
            }),
            sites = sites.Select(s => new
            {
                host = s.Host,
                url = s.Url,
                title = s.Title,
                letter = SiteBadge.Letter(s.Url) ?? "?",
                color = SiteBadge.Palette[SiteBadge.ColorIndex(s.Url)],
            }),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
