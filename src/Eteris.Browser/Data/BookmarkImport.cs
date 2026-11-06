using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Data;

public sealed record ImportSource(string Id, string Name, string Path, int Count);

// Importar favoritos de navegadores Chromium (Chrome, Edge, Brave): o ficheiro «Bookmarks» é JSON.
// Só lê; nunca altera nada no outro navegador.
public static class BookmarkImport
{
    private static readonly (string Id, string Name, string Relative)[] Known =
    [
        ("chrome", "Google Chrome", @"Google\Chrome\User Data\Default\Bookmarks"),
        ("edge", "Microsoft Edge", @"Microsoft\Edge\User Data\Default\Bookmarks"),
        ("brave", "Brave", @"BraveSoftware\Brave-Browser\User Data\Default\Bookmarks"),
    ];

    public static IReadOnlyList<ImportSource> Detect(string localAppData)
    {
        var found = new List<ImportSource>();
        foreach (var (id, name, relative) in Known)
        {
            var path = System.IO.Path.Combine(localAppData, relative);
            if (!File.Exists(path)) continue;
            var count = Read(path).Count;
            if (count > 0) found.Add(new ImportSource(id, name, path, count));
        }
        return found;
    }

    public static IReadOnlyList<Bookmark> Read(string path)
    {
        try { return ParseChromium(File.ReadAllText(path)); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static IReadOnlyList<Bookmark> ParseChromium(string json)
    {
        var marks = new List<Bookmark>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("roots", out var roots)) return marks;
            foreach (var root in roots.EnumerateObject())
                if (root.Value.ValueKind == JsonValueKind.Object) Walk(root.Value, marks);
        }
        catch (JsonException) { }
        return marks;
    }

    private static void Walk(JsonElement node, List<Bookmark> marks)
    {
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "url" && node.TryGetProperty("url", out var u) && u.GetString() is { } url
            && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            var title = node.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (title.Length == 0) title = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            marks.Add(new Bookmark(url, title));
        }
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Walk(child, marks);
    }

    // Devolve quantos foram acrescentados (os que já existem ficam como estão).
    public static int Import(BookmarkStore store, IReadOnlyList<Bookmark> marks, DateTime nowUtc)
    {
        var added = 0;
        foreach (var mark in marks)
        {
            if (store.Contains(mark.Url)) continue;
            store.Toggle(mark.Url, mark.Title, nowUtc);
            added++;
        }
        return added;
    }
}
