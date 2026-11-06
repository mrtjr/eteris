namespace Eteris.Browser.Omnibox;

public sealed record OmniboxResult(string Url, bool IsSearch, string? Query = null);

public static class OmniboxClassifier
{
    private static readonly HashSet<string> NavigableSchemes = new(StringComparer.OrdinalIgnoreCase)
        { "http", "https", "eteris", "file", "about" };

    public static OmniboxResult? Classify(string input, SearchEngine engine)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;
        if (text[0] == '?') return Search(text[1..].Trim(), engine);
        if (text.Any(char.IsWhiteSpace)) return Search(text, engine);

        var colon = text.IndexOf(':');
        if (colon > 0 && NavigableSchemes.Contains(text[..colon]) && Uri.TryCreate(text, UriKind.Absolute, out _))
            return new OmniboxResult(text, IsSearch: false);

        var host = HostPart(text);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsDottedIPv4(host))
            return new OmniboxResult("http://" + text, IsSearch: false);
        if (LooksLikeDomain(host))
            return new OmniboxResult("https://" + text, IsSearch: false);

        return Search(text, engine);
    }

    private static OmniboxResult Search(string query, SearchEngine engine)
        => new(engine.UrlFor(query), IsSearch: true, query);

    private static string HostPart(string text)
    {
        var end = text.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? text : text[..end];
        var colon = authority.LastIndexOf(':');
        return colon > 0 && authority[(colon + 1)..].All(char.IsAsciiDigit) && colon < authority.Length - 1
            ? authority[..colon]
            : authority;
    }

    private static bool IsDottedIPv4(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p) <= 255);
    }

    private static bool LooksLikeDomain(string host)
    {
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0)) return false;
        if (!labels.All(l => l.All(c => char.IsLetterOrDigit(c) || c == '-'))) return false;
        var tld = labels[^1];
        return tld.Length >= 2 && tld.All(char.IsLetter);
    }
}
