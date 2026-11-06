using System.IO;
using System.Text.Json;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Browser;

// eteris://bookmarks — gestor de favoritos (pesquisar, renomear, remover). Ações protegidas pelo token da sessão.
public static class BookmarksPage
{
    public const string Url = "eteris://bookmarks/";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(BookmarksPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.bookmarks.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly System.Globalization.CultureInfo PtCulture = new("pt-PT");

    public static string Render(IReadOnlyList<Bookmark> bookmarks, IReadOnlyList<ReadingItem>? reading = null,
        IReadOnlyList<SavedSession>? sessions = null)
    {
        var data = new
        {
            token = InternalPages.Token,
            sessions = (sessions ?? []).Select(s => new
            {
                id = s.Id,
                name = s.Name,
                count = s.Tabs.Count,
                when = s.CreatedUtc.ToLocalTime().ToString("d MMM, HH:mm", PtCulture),
                titles = s.Tabs.Take(4).Select(t => t.Title.Length > 0 ? t.Title : t.Url),
                badges = s.Tabs.Take(4).Select(t => new { letter = SiteBadge.Letter(t.Url) ?? "?", color = SiteBadge.Palette[SiteBadge.ColorIndex(t.Url)] }),
            }),
            reading = (reading ?? []).Select(r => new
            {
                url = r.Url,
                title = r.Title.Length > 0 ? r.Title : r.Url,
                host = Uri.TryCreate(r.Url, UriKind.Absolute, out var ru) ? ru.Host : r.Url,
                letter = SiteBadge.Letter(r.Url) ?? "?",
                color = SiteBadge.Palette[SiteBadge.ColorIndex(r.Url)],
                read = r.Read,
            }),
            items = bookmarks.Select(b => new
            {
                url = b.Url,
                title = b.Title.Length > 0 ? b.Title : b.Url,
                host = Uri.TryCreate(b.Url, UriKind.Absolute, out var u) ? u.Host : b.Url,
                letter = SiteBadge.Letter(b.Url) ?? "?",
                color = SiteBadge.Palette[SiteBadge.ColorIndex(b.Url)],
            }),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
