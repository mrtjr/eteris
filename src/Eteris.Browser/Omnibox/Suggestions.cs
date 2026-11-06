using Eteris.Browser.Data;

namespace Eteris.Browser.Omnibox;

public enum SuggestionKind { Search, Navigate, History, Bookmark, Tab, Answer }

public sealed record Suggestion(SuggestionKind Kind, string Text, string Detail, string Url);

public static class Suggestions
{
    public const int MaxHistory = 6;

    public static IReadOnlyList<Suggestion> Build(string input, SearchEngine engine, IReadOnlyList<HistoryEntry> history,
        IReadOnlyList<HistoryEntry>? bookmarks = null)
    {
        var primary = OmniboxClassifier.Classify(input, engine);
        if (primary is null) return [];

        var list = new List<Suggestion>
        {
            primary.IsSearch
                ? new Suggestion(SuggestionKind.Search, primary.Query!, $"Pesquisar com {engine.Name}", primary.Url)
                : new Suggestion(SuggestionKind.Navigate, input.Trim(), "Abrir endereço", primary.Url),
        };

        var marked = (bookmarks ?? []).Where(b => !SameUrl(b.Url, primary.Url)).Take(3).ToList();
        list.AddRange(marked.Select(b => new Suggestion(SuggestionKind.Bookmark, b.Title.Length > 0 ? b.Title : b.Url, b.Url, b.Url)));

        list.AddRange(history
            .Where(h => !SameUrl(h.Url, primary.Url) && !marked.Any(b => SameUrl(b.Url, h.Url)))
            .Take(MaxHistory)
            .Select(h => new Suggestion(SuggestionKind.History, h.Title.Length > 0 ? h.Title : h.Url, h.Url, h.Url)));
        return list;
    }

    // Completa o domínio da melhor entrada do histórico mantendo o que o utilizador escreveu (incluindo maiúsculas).
    public static string? InlineCompletion(string typed, IReadOnlyList<HistoryEntry> history)
    {
        if (typed.Length == 0 || typed.Any(char.IsWhiteSpace) || typed.IndexOfAny(['/', '?', '#', ':']) >= 0) return null;

        foreach (var entry in history)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri)) continue;
            var host = uri.Host;
            foreach (var candidate in new[] { host, host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host })
            {
                if (candidate.Length > typed.Length && candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    return typed + candidate[typed.Length..];
            }
        }
        return null;
    }

    private static bool SameUrl(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
