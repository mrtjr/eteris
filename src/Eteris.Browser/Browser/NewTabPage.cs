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
    public static string Render(IReadOnlyList<TopSite> sites, SearchEngine engine, DateTime now, int blocked = 0)
    {
        var data = new
        {
            greeting = Greeting(now.Hour),
            blocked,
            engineName = engine.Name,
            engineTemplate = engine.Template,
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
