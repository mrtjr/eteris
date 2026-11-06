namespace Eteris.Browser.Browser;

// «Apagar dados de navegação»: o histórico respeita o período; cookies e cache são apagados por completo.
public sealed record ClearDataRequest(bool History, bool Cookies, bool Cache, DateTime Since)
{
    public static ClearDataRequest? Parse(string what, string range, DateTime now)
    {
        var parts = what.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p is not ("history" or "cookies" or "cache"))) return null;
        if (HistoryPage.RangeStart(range, now) is not { } since) return null;
        return new ClearDataRequest(parts.Contains("history"), parts.Contains("cookies"), parts.Contains("cache"), since);
    }

    public string Summary
    {
        get
        {
            var names = new List<string>();
            if (History) names.Add("histórico");
            if (Cookies) names.Add("cookies");
            if (Cache) names.Add("cache");
            var text = names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " e " + names[^1];
            return char.ToUpperInvariant(text[0]) + text[1..] + " apagados";
        }
    }
}
