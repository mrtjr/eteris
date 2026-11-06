namespace Eteris.Browser.Shields;

// Remove dos links os parâmetros que só servem para rastrear de onde veio o clique (utm_*, fbclid, gclid…).
// Lista conservadora: só parâmetros que nunca mudam o conteúdo da página.
public static class UrlCleaner
{
    private static readonly HashSet<string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "gclsrc", "msclkid", "yclid", "twclid", "ttclid", "li_fat_id",
        "mc_cid", "mc_eid", "igshid", "_hsenc", "_hsmi", "__hssc", "__hstc", "__hsfp", "hsctatracking", "mkt_tok",
        "oly_anon_id", "oly_enc_id", "vero_conv", "vero_id", "rb_clickid", "s_cid", "ga_source", "ga_medium",
        "ga_campaign", "ga_content", "ga_term", "_openstat", "wickedid", "srsltid",
    };

    public static string Clean(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return url;
        var fragmentStart = url.IndexOf('#', queryStart);
        var query = fragmentStart < 0 ? url[(queryStart + 1)..] : url[(queryStart + 1)..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : url[fragmentStart..];

        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Where(p => !IsTracking(p.Split('=', 2)[0])).ToList();
        if (kept.Count == parts.Length) return url;
        var head = url[..queryStart];
        return head + (kept.Count > 0 ? "?" + string.Join('&', kept) : string.Empty) + fragment;
    }

    private static bool IsTracking(string name)
    {
        var key = Uri.UnescapeDataString(name);
        return key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || Exact.Contains(key);
    }
}
