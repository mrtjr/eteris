using System.IO;
using System.Net.Http;
using System.Text;

namespace Eteris.Browser.Shields;

// As listas que o uBlock Origin Lite ativa por omissão, mais a EasyList Portuguesa.
// Há uma cópia junto do exe; a cópia descarregada (atualizada semanalmente) tem prioridade.
public sealed record ListSummary(string Name, int Rules, DateTime UpdatedUtc, bool Bundled);

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
        ["ublock-cookies-easylist"] = "https://ublockorigin.github.io/uAssets/thirdparties/easylist-cookies.txt",
        ["ublock-cookies-adguard"] = "https://ublockorigin.github.io/uAssets/filters/annoyances-cookies.txt",
    };

    // O uBlock confia nas listas que ele próprio mantém (ids "ublock-"): podem usar scriptlets "trusted".
    public static bool IsTrusted(string name) => name.StartsWith("ublock-", StringComparison.Ordinal);

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private const int MinimumListLength = 1000;

    public static FilterLists ForApp() => new(
        Path.Combine(AppContext.BaseDirectory, "Shields", "lists"),
        Infrastructure.AppPaths.Filters);

    public string LoadAll() => Read(Sources.Keys);

    public string Load(bool trusted) => Read(Sources.Keys.Where(name => IsTrusted(name) == trusted));

    // Os ficheiros das listas em uso (a cópia descarregada ou, sem ela, a embutida), pela ordem de Sources: o motor lê-os
    // diretamente (AdblockEngine.CreateFromFiles), sem o texto passar pelo C#.
    public IReadOnlyList<string> Paths(bool trusted)
        => Sources.Keys.Where(name => IsTrusted(name) == trusted).Select(PathOf).Where(File.Exists).ToList();

    private string PathOf(string name)
    {
        var cached = Path.Combine(cacheDir, name + ".txt");
        return File.Exists(cached) ? cached : Path.Combine(bundledDir, name + ".txt");
    }

    private string Read(IEnumerable<string> names)
    {
        var text = new StringBuilder();
        foreach (var path in names.Select(PathOf))
        {
            if (!File.Exists(path)) continue;
            text.Append(File.ReadAllText(path)).Append('\n');
        }
        return text.ToString();
    }

    private const string EnginePrefix = "motor-";
    private const string EngineExtension = ".dat";

    // O motor já compilado destas listas, na pasta do perfil (como o Brave guarda o dele). O nome resume tudo o que o
    // muda: cada lista em uso (caminho, tamanho e data, normal ou de confiança) e «version» (a versão do Éteris e do
    // motor). Uma lista atualizada ou um Éteris novo dão outro nome: a cópia antiga nunca é usada por engano.
    public string EngineCachePath(string version)
    {
        var key = new StringBuilder(version).Append('\n');
        foreach (var trusted in new[] { false, true })
            foreach (var path in Paths(trusted))
            {
                var info = new FileInfo(path);
                key.Append(trusted ? 'T' : 'N').Append('|').Append(path.ToLowerInvariant()).Append('|')
                    .Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..16];
        return Path.Combine(cacheDir, EnginePrefix + hash.ToLowerInvariant() + EngineExtension);
    }

    // Apaga os motores compilados de listas ou versões antigas (e restos de gravações interrompidas), menos «keep».
    public void RemoveOtherEngineCaches(string keep)
    {
        if (!Directory.Exists(cacheDir)) return;
        foreach (var path in Directory.EnumerateFiles(cacheDir, EnginePrefix + "*"))
        {
            if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Para a página «Sobre»: regras de cada lista e de quando é a cópia em uso.
    public IReadOnlyList<ListSummary> Summary()
        => Sources.Keys.Select(name =>
        {
            var cached = Path.Combine(cacheDir, name + ".txt");
            var bundled = !File.Exists(cached);
            var path = bundled ? Path.Combine(bundledDir, name + ".txt") : cached;
            if (!File.Exists(path)) return new ListSummary(name, 0, DateTime.MinValue, bundled);
            var rules = File.ReadLines(path).Count(line => line.Length > 0 && line[0] is not '!' and not '[');
            return new ListSummary(name, rules, File.GetLastWriteTimeUtc(path), bundled);
        }).ToList();

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
