using System.IO;
using System.Net.Http;
using System.Text;

namespace Eteris.Browser.Shields;

// As listas que o uBlock Origin Lite ativa por omissão, mais a EasyList Portuguesa.
// Há uma cópia junto do exe; a cópia descarregada (atualizada semanalmente) tem prioridade.
public sealed class FilterLists(string bundledDir, string cacheDir)
{
    public static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>
    {
        ["ublock-filters"] = "https://ublockorigin.github.io/uAssets/filters/filters.min.txt",
        ["ublock-privacy"] = "https://ublockorigin.github.io/uAssets/filters/privacy.min.txt",
        ["ublock-unbreak"] = "https://ublockorigin.github.io/uAssets/filters/unbreak.min.txt",
        ["ublock-badware"] = "https://ublockorigin.github.io/uAssets/filters/badware.min.txt",
        ["ublock-quick-fixes"] = "https://ublockorigin.github.io/uAssets/filters/quick-fixes.min.txt",
        ["easylist"] = "https://ublockorigin.github.io/uAssets/thirdparties/easylist.txt",
        ["easyprivacy"] = "https://ublockorigin.github.io/uAssets/thirdparties/easyprivacy.txt",
        ["easylist-portuguese"] = "https://easylist-downloads.adblockplus.org/easylistportuguese.txt",
    };

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private const int MinimumListLength = 1000;

    public static FilterLists ForApp() => new(
        Path.Combine(AppContext.BaseDirectory, "Shields", "lists"),
        Infrastructure.AppPaths.Filters);

    public string LoadAll()
    {
        var text = new StringBuilder();
        foreach (var name in Sources.Keys)
        {
            var cached = Path.Combine(cacheDir, name + ".txt");
            var path = File.Exists(cached) ? cached : Path.Combine(bundledDir, name + ".txt");
            if (!File.Exists(path)) continue;
            text.Append(File.ReadAllText(path)).Append('\n');
        }
        return text.ToString();
    }

    public bool IsStale(DateTime nowUtc)
        => Sources.Keys.Any(name =>
        {
            var path = Path.Combine(cacheDir, name + ".txt");
            return !File.Exists(path) || nowUtc - File.GetLastWriteTimeUtc(path) > MaxAge;
        });

    // Devolve true se pelo menos uma lista foi atualizada. Uma resposta inválida nunca substitui a cópia anterior.
    public async Task<bool> UpdateAsync(HttpClient http, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDir);
        var updated = false;
        foreach (var (name, url) in Sources)
        {
            try
            {
                using var response = await http.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!LooksLikeFilterList(body)) continue;

                var target = Path.Combine(cacheDir, name + ".txt");
                var temporary = target + ".tmp";
                await File.WriteAllTextAsync(temporary, body, cancellationToken);
                File.Move(temporary, target, overwrite: true);
                updated = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { }
        }
        return updated;
    }

    private static bool LooksLikeFilterList(string body)
        => body.Length >= MinimumListLength && body.TrimStart() is { Length: > 0 } trimmed && trimmed[0] != '<';
}
