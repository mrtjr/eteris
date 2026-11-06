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

    public static string Render(IReadOnlyList<Bookmark> bookmarks, IReadOnlyList<ReadingItem>? reading = null)
    {
        var data = new
        {
            token = InternalPages.Token,
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
