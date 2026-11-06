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
        IReadOnlyList<Bookmark>? bookmarks = null, bool isPrivate = false, int[]? week = null, UserSettings? prefs = null,
        IReadOnlyList<ReadingItem>? reading = null, IReadOnlyList<SavedSession>? sessions = null)
    {
        // Numa janela privada não se mostra nada do perfil normal.
        if (isPrivate)
        {
            sites = [];
            bookmarks = [];
            reading = [];
            sessions = [];
        }

        var data = new
        {
            @private = isPrivate,
            greeting = Greeting(now.Hour),
            blocked,
            token = InternalPages.Token,
            theme = (prefs ?? new UserSettings()).NewTabTheme,
            showClock = (prefs ?? new UserSettings()).NewTabClock,
            showTiles = (prefs ?? new UserSettings()).NewTabTiles,
            showWeek = (prefs ?? new UserSettings()).NewTabWeek,
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
            resume = Resume(reading ?? [], sessions ?? []),
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

    // «Continuar»: até duas sessões guardadas e o resto em artigos por ler — no máximo quatro cartões.
    public static IReadOnlyList<ResumeCard> Resume(IReadOnlyList<ReadingItem> reading, IReadOnlyList<SavedSession> sessions)
    {
        var cards = sessions.Where(s => s.Tabs.Count > 0).Take(2).Select(s => new ResumeCard(
            "session", s.Name, s.Tabs.Count == 1 ? "Sessão · 1 aba" : $"Sessão · {s.Tabs.Count} abas", null, s.Id,
            SiteBadge.Letter(s.Tabs[0].Url) ?? "?", SiteBadge.Palette[SiteBadge.ColorIndex(s.Tabs[0].Url)])).ToList();
        foreach (var item in reading.Where(r => !r.Read).Take(4 - cards.Count))
        {
            var host = Uri.TryCreate(item.Url, UriKind.Absolute, out var u) ? u.Host : item.Url;
            cards.Add(new ResumeCard("read", item.Title.Length > 0 ? item.Title : item.Url, "Para ler · " + host, item.Url, 0,
                SiteBadge.Letter(item.Url) ?? "?", SiteBadge.Palette[SiteBadge.ColorIndex(item.Url)]));
        }
        return cards;
    }
}

public sealed record ResumeCard(string Kind, string Title, string Subtitle, string? Url, long SessionId, string Letter, string Color);
