using System.Net.Http;
using System.Text.Json;

namespace Eteris.Browser.Omnibox;

// Sugestões do motor de pesquisa (opcional, desligado por omissão: envia o que se escreve ao motor).
public static class SearchSuggest
{
    public const int MaxSuggestions = 4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1.5) };

    static SearchSuggest() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Eteris/1.0");

    public static string? EndpointFor(SearchEngine engine, string query)
    {
        var q = Uri.EscapeDataString(query);
        if (engine == SearchEngine.DuckDuckGo) return $"https://duckduckgo.com/ac/?q={q}&type=list";
        if (engine == SearchEngine.Brave) return $"https://search.brave.com/api/suggest?q={q}";
        return null;
    }

    // Só pesquisas de verdade: nada de endereços, bangs, contas ou texto curtíssimo.
    public static bool ShouldQuery(string input, SearchEngine engine)
    {
        var text = input.Trim();
        if (text.Length < 2 || text.StartsWith('!') || InstantAnswer.Answer(text) is not null) return false;
        return OmniboxClassifier.Classify(text, engine) is { IsSearch: true };
    }

    // Formato OpenSearch: ["consulta", ["sugestão 1", "sugestão 2", ...]]
    public static IReadOnlyList<string> Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2) return [];
            var list = doc.RootElement[1];
            return list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
        }
        catch (JsonException) { return []; }
    }

    public static async Task<IReadOnlyList<string>> FetchAsync(SearchEngine engine, string query, CancellationToken token)
    {
        if (EndpointFor(engine, query) is not { } url) return [];
        try { return Parse(await Http.GetStringAsync(url, token)); }
        catch (Exception) { return []; }
    }

    // Junta as sugestões logo a seguir à linha principal, sem repetir o que já está na lista.
    public static IReadOnlyList<Suggestion> Merge(IReadOnlyList<Suggestion> list, IReadOnlyList<string> remote, SearchEngine engine)
    {
        var primary = list.ToList().FindIndex(s => s.Kind != SuggestionKind.Answer);
        if (primary < 0) return list;
        var existing = new HashSet<string>(list.Select(s => s.Text), StringComparer.OrdinalIgnoreCase);
        var extra = remote.Where(r => existing.Add(r)).Take(MaxSuggestions)
            .Select(r => new Suggestion(SuggestionKind.Search, r, $"Pesquisar com {engine.Name}", engine.UrlFor(r)));
        var merged = list.ToList();
        merged.InsertRange(primary + 1, extra);
        return merged;
    }
}
