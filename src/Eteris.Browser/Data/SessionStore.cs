using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Data;

public sealed record Session(IReadOnlyList<string> Urls, int ActiveIndex, IReadOnlyList<string>? Titles = null,
    IReadOnlyList<bool>? Pinned = null);

// Abas abertas, gravadas continuamente para sobreviverem a um fecho normal ou a um crash.
public sealed class SessionStore(string path)
{
    private const int MaxTabs = 100;

    public void Save(IEnumerable<string> urls, int activeIndex, IEnumerable<string>? titles = null, IEnumerable<bool>? pinned = null)
    {
        var all = urls.ToList();
        var allTitles = titles?.ToList() ?? [];
        var allPinned = pinned?.ToList() ?? [];
        var keptIndices = Enumerable.Range(0, all.Count).Where(i => IsRestorable(all[i])).Take(MaxTabs).ToList();
        var kept = keptIndices.Select(i => all[i]).ToList();
        var keptTitles = keptIndices.Select(i => i < allTitles.Count ? allTitles[i] : string.Empty).ToList();
        var keptPinned = keptIndices.Select(i => i < allPinned.Count && allPinned[i]).ToList();
        var active = Math.Max(0, keptIndices.IndexOf(activeIndex));

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Session(kept, active, keptTitles, keptPinned)));
        File.Move(temporary, path, overwrite: true);
    }

    public Session? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(path));
            if (session?.Urls is null) return null;
            var indices = Enumerable.Range(0, session.Urls.Count).Where(i => IsRestorable(session.Urls[i])).ToList();
            var urls = indices.Select(i => session.Urls[i]).ToList();
            if (urls.All(u => u.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase))) return null;
            var titles = indices.Select(i => session.Titles is { } t && i < t.Count ? t[i] : string.Empty).ToList();
            var pinned = indices.Select(i => session.Pinned is { } p && i < p.Count && p[i]).ToList();
            return new Session(urls, Math.Clamp(session.ActiveIndex, 0, urls.Count - 1), titles, pinned);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static bool IsRestorable(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase);
}
