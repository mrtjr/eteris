using System.IO;
using System.Text.Json;

namespace Eteris.Browser.Data;

public sealed record Session(IReadOnlyList<string> Urls, int ActiveIndex, IReadOnlyList<string>? Titles = null,
    IReadOnlyList<bool>? Pinned = null, IReadOnlyList<int>? GroupIds = null, IReadOnlyList<SessionGroup>? Groups = null,
    SessionSplit? Split = null, IReadOnlyList<SessionTabMeta>? Metas = null);

// O que o utilizador deu à aba (Tabs/TabIdentity.cs): nome próprio, emoji e cor do ícone; tudo opcional.
public sealed record SessionTabMeta(string? Name = null, string? Emoji = null, int? Color = null)
{
    public static readonly SessionTabMeta Empty = new();

    public bool IsEmpty => Name is null && Emoji is null && Color is null;
}

// Ecrã dividido gravado na sessão: índices das duas abas (na lista gravada) e a orientação («side» / «stacked»).
public sealed record SessionSplit(int Left, int Right, string Orientation)
{
    public bool IsValid(int tabCount) => Left != Right && Left >= 0 && Right >= 0 && Left < tabCount && Right < tabCount;
}

// Grupo de abas gravado na sessão; cada aba guarda o índice do seu grupo (-1 = nenhum).
public sealed record SessionGroup(string Name, int ColorIndex, bool Collapsed);

// Abas abertas, gravadas continuamente para sobreviverem a um fecho normal ou a um crash.
public sealed class SessionStore(string path)
{
    private const int MaxTabs = 100;

    // Campos a null (divisão, identidades) ficam fora do ficheiro: lê-se da mesma e o JSON fica legível.
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public void Save(IEnumerable<string> urls, int activeIndex, IEnumerable<string>? titles = null, IEnumerable<bool>? pinned = null,
        IEnumerable<int>? groupIds = null, IReadOnlyList<SessionGroup>? groups = null, SessionSplit? split = null,
        IEnumerable<SessionTabMeta?>? metas = null)
    {
        var allGroupIds = groupIds?.ToList() ?? [];
        var allMetas = metas?.ToList() ?? [];
        var all = urls.ToList();
        var allTitles = titles?.ToList() ?? [];
        var allPinned = pinned?.ToList() ?? [];
        var keptIndices = Enumerable.Range(0, all.Count).Where(i => IsRestorable(all[i])).Take(MaxTabs).ToList();
        var kept = keptIndices.Select(i => all[i]).ToList();
        var keptTitles = keptIndices.Select(i => i < allTitles.Count ? allTitles[i] : string.Empty).ToList();
        var keptPinned = keptIndices.Select(i => i < allPinned.Count && allPinned[i]).ToList();
        var active = Math.Max(0, keptIndices.IndexOf(activeIndex));
        var keptGroupIds = keptIndices.Select(i => i < allGroupIds.Count ? allGroupIds[i] : -1).ToList();
        var keptSplit = Remap(split, keptIndices);
        var keptMetas = keptIndices.Select(i => i < allMetas.Count ? allMetas[i] ?? SessionTabMeta.Empty : SessionTabMeta.Empty).ToList();
        // Sem nenhuma aba com nome, emoji ou cor, o campo nem vai para o ficheiro.
        var metasOrNull = keptMetas.All(m => m.IsEmpty) ? null : keptMetas;

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Session(kept, active, keptTitles, keptPinned, keptGroupIds, groups ?? [], keptSplit, metasOrNull), Options));
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
            var groupCount = session.Groups?.Count ?? 0;
            var groupIds = indices.Select(i => session.GroupIds is { } g && i < g.Count && g[i] < groupCount ? g[i] : -1).ToList();
            var metas = indices.Select(i => session.Metas is { } m && i < m.Count ? m[i] ?? SessionTabMeta.Empty : SessionTabMeta.Empty).ToList();
            return new Session(urls, Math.Clamp(session.ActiveIndex, 0, urls.Count - 1), titles, pinned, groupIds, session.Groups ?? [],
                Remap(session.Split, indices), metas);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    // A divisão só sobrevive se as duas abas sobreviverem à filtragem; os índices passam a ser os da lista filtrada.
    private static SessionSplit? Remap(SessionSplit? split, List<int> keptIndices)
    {
        if (split is null) return null;
        var left = keptIndices.IndexOf(split.Left);
        var right = keptIndices.IndexOf(split.Right);
        var remapped = new SessionSplit(left, right, split.Orientation ?? string.Empty);
        return remapped.IsValid(keptIndices.Count) ? remapped : null;
    }

    private static bool IsRestorable(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase);
}
