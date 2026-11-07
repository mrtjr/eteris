using System.Text;

namespace Eteris.Browser.Shields;

// Saltar redirecionamentos de rastreio («debounce», como o Brave): um link que passa por l.facebook.com/l.php,
// google.com/url, safelinks do Outlook, bing.com/ck/a… leva o destino no próprio endereço. O Éteris vai direto ao
// destino e o intermediário nunca chega a saber do clique. Regras próprias (anfitrião, caminho, parâmetro) e, para
// anfitriões que as listas dão como rastreadores, os parâmetros óbvios (url, u, redirect, destination…).
// Só destinos http/https bem formados, sem credenciais no endereço; um destino que é outro salto também é saltado.
public static class Debounce
{
    // Host "google.*" vale para qualquer domínio do Google (google.com, google.pt, google.com.br…).
    public sealed record Rule(string Host, string PathPrefix, string[] Params, string? Base64Prefix = null);

    public static readonly IReadOnlyList<Rule> Rules =
    [
        new("l.facebook.com", "/l.php", ["u"]),
        new("lm.facebook.com", "/l.php", ["u"]),
        new("l.messenger.com", "/l.php", ["u"]),
        new("l.instagram.com", "/", ["u"]),
        new("l.threads.net", "/", ["u"]),
        new("l.threads.com", "/", ["u"]),
        new("google.*", "/url", ["q", "url"]),
        new("googleadservices.com", "/pagead/aclk", ["adurl"]),
        new("clickserve.dartsearch.net", "/link/click", ["ds_dest_url"]),
        new("safelinks.protection.outlook.com", "/", ["url"]),
        new("youtube.com", "/redirect", ["q"]),
        new("out.reddit.com", "/", ["url"]),
        new("steamcommunity.com", "/linkfilter", ["u", "url"]),
        new("linkedin.com", "/safety/go", ["url"]),
        new("vk.com", "/away.php", ["to"]),
        new("slack-redir.net", "/link", ["url"]),
        new("duckduckgo.com", "/l/", ["uddg"]),
        new("bing.com", "/ck/a", ["u"], Base64Prefix: "a1"),
        new("t.umblr.com", "/redirect", ["z"]),
        new("pinterest.com", "/offsite", ["url"]),
        new("medium.com", "/r/", ["url"]),
        new("getpocket.com", "/redirect", ["url"]),
        new("go.skimresources.com", "/", ["url"]),
        new("go.redirectingat.com", "/", ["url"]),
        new("click.linksynergy.com", "/", ["murl"]),
        new("awin1.com", "/cread.php", ["ued"]),
        new("exit.sc", "/", ["url"]),
        new("deref-gmx.net", "/", ["redirectUrl"]),
        new("deref-web.de", "/", ["redirectUrl"]),
    ];

    // Parâmetros que, num anfitrião de rastreio das listas, levam o destino. Sem redirect_uri (OAuth: não é um salto).
    public static readonly IReadOnlyList<string> TrackerParams =
        ["url", "u", "redirect", "redirect_url", "redirectUrl", "destination", "dest", "target", "to", "goto", "link"];

    // Saltos encadeados (google.com/url → l.facebook.com/l.php → destino) saltam-se de uma vez, até este limite.
    private const int MaxHops = 3;

    // O destino final do link, ou null quando não há salto a evitar.
    public static string? Destination(string url, Func<string, bool>? isTrackerHost = null)
    {
        string? result = null;
        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (Step(result ?? url, isTrackerHost) is not { } next) break;
            result = next;
        }
        return result;
    }

    // Anfitrião do salto (para o Escudo), ou null quando o endereço não é um salto conhecido.
    public static string? RedirectorHost(string url, Func<string, bool>? isTrackerHost = null)
        => Step(url, isTrackerHost) is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

    private static string? Step(string url, Func<string, bool>? isTrackerHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsWeb(uri) || uri.Query.Length < 2) return null;
        var host = uri.Host.ToLowerInvariant();
        var query = ParseQuery(uri.Query);

        foreach (var rule in Rules)
        {
            if (!HostMatches(host, rule.Host) || !uri.AbsolutePath.StartsWith(rule.PathPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in rule.Params)
                if (query.TryGetValue(name, out var value) && Decode(value, rule.Base64Prefix) is { } target) return target;
        }

        if (isTrackerHost?.Invoke(host) != true) return null;
        foreach (var name in TrackerParams)
            if (query.TryGetValue(name, out var value) && Decode(value, null) is { } target) return target;
        return null;
    }

    private static bool HostMatches(string host, string pattern)
    {
        if (pattern == "google.*")
            return TrackerBlocker.RegistrableDomain(host).StartsWith("google.", StringComparison.Ordinal);
        return host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal);
    }

    // Primeiro valor de cada parâmetro (nomes sem distinguir maiúsculas), já descodificado como num formulário.
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length < 2) continue;
            var name = Unescape(pair[0]);
            if (name is null || result.ContainsKey(name)) continue;
            if (Unescape(pair[1]) is { } value) result[name] = value;
        }
        return result;
    }

    private static string? Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return null; }
    }

    // O valor do parâmetro como endereço http/https absoluto (aceita uma segunda codificação e base64url com prefixo).
    public static string? Decode(string value, string? base64Prefix)
    {
        var text = value.Trim();
        if (base64Prefix is not null)
        {
            if (!text.StartsWith(base64Prefix, StringComparison.Ordinal)) return null;
            text = FromBase64Url(text[base64Prefix.Length..]);
            if (text is null) return null;
        }
        // Codificado duas vezes (https%3A%2F%2F…): uma descodificação a mais, nunca mais do que uma.
        if (!text.Contains("://", StringComparison.Ordinal) && text.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && Unescape(text) is { } again)
            text = again;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (text.Any(c => char.IsControl(c) || c == ' ')) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var target) || !IsWeb(target) || target.Host.Length == 0) return null;
        // «https://banco.pt@outro.site» engana quem lê; um destino com credenciais não é seguido.
        if (target.UserInfo.Length > 0) return null;
        return target.AbsoluteUri;
    }

    private static string? FromBase64Url(string text)
    {
        var b64 = text.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { return null; }
    }

    private static bool IsWeb(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}

// Textos do Escudo para os saltos evitados nesta página.
public static class DebounceText
{
    public static string ShieldLine(IReadOnlyList<string> skipped, bool enabled)
    {
        if (!enabled) return "·  Saltos de rastreio nos links: desligado nas definições";
        if (skipped.Count == 0) return "✓  Os links que passam por rastreadores vão direto ao destino";
        var hosts = skipped.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var count = skipped.Count == 1 ? "1 salto de rastreio removido" : $"{skipped.Count} saltos de rastreio removidos";
        return $"✓  {count}: {hosts[0]}" + (hosts.Count > 1 ? $" e mais {hosts.Count - 1}" : string.Empty);
    }

    public static string Tooltip(int skipped)
        => skipped == 0 ? string.Empty : skipped == 1 ? " · 1 salto de rastreio removido" : $" · {skipped} saltos de rastreio removidos";
}
