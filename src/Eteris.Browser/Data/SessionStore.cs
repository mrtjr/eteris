using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Data;

public sealed record Session(IReadOnlyList<string> Urls, int ActiveIndex);

// Abas abertas, gravadas continuamente para sobreviverem a um fecho normal ou a um crash.
public sealed class SessionStore(string path)
{
    private const int MaxTabs = 100;

    public void Save(IEnumerable<string> urls, int activeIndex)
    {
        var all = urls.ToList();
        var kept = all.Where(IsRestorable).Take(MaxTabs).ToList();
        var active = activeIndex >= 0 && activeIndex < all.Count && IsRestorable(all[activeIndex])
            ? kept.IndexOf(all[activeIndex])
            : 0;
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Session(kept, Math.Max(0, active))));
        File.Move(temporary, path, overwrite: true);
    }

    public Session? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(path));
            if (session?.Urls is null) return null;
            var urls = session.Urls.Where(IsRestorable).ToList();
            if (urls.All(u => u.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase))) return null;
            return new Session(urls, Math.Clamp(session.ActiveIndex, 0, urls.Count - 1));
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
