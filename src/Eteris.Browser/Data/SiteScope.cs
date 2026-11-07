namespace Eteris.Browser.Data;

// O «site» de «Esquecer este site ao fechar» e de «Triturar»: o domínio registável (eTLD+1) — exemplo.pt cobre
// www.exemplo.pt e loja.exemplo.pt, como os cookies de .exemplo.pt — e nunca outro site com nome parecido
// (malexemplo.pt não é exemplo.pt). Só páginas da web (http/https); um IP é o próprio site.
public static class SiteScope
{
    public static string? SiteOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? OfHost(uri.Host)
            : null;

    public static string OfHost(string host) => Shields.TrackerBlocker.RegistrableDomain(host.Trim('.').ToLowerInvariant());

    public static bool Belongs(string? url, string site) => SiteOf(url) is { } own && own == site;

    // Domínio de um cookie (".exemplo.pt", "www.exemplo.pt") dentro do site.
    public static bool CookieBelongs(string cookieDomain, string site)
    {
        var host = cookieDomain.Trim().TrimStart('.').ToLowerInvariant();
        return host.Length > 0 && (host == site || host.EndsWith("." + site, StringComparison.Ordinal));
    }

    // Endereço para apagar um cookie pelo motor (o CookieManager apaga por URL + nome).
    public static string CookieUrl(string cookieDomain, string path)
        => "https://" + cookieDomain.Trim().TrimStart('.') + (path.StartsWith('/') ? path : "/" + path);

    // As origens (esquema://host[:porta]) do site vistas nos endereços dados — abas abertas, histórico — mais as duas
    // óbvias (com e sem www, em https e http): é por origem que o armazenamento se apaga.
    public static IReadOnlyList<string> Origins(string site, IEnumerable<string> seenUrls)
    {
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var host in new[] { site, "www." + site })
            foreach (var scheme in new[] { "https", "http" })
                origins.Add($"{scheme}://{host}");
        foreach (var url in seenUrls)
        {
            if (!Belongs(url, site) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            origins.Add(uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}" : $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port}");
        }
        return origins.ToList();
    }

    // Fechou-se uma aba: ainda há alguma do site em qualquer janela (a dormir, noutros Espaços)? Só sem nenhuma é que
    // «Esquecer ao fechar» apaga.
    public static bool StillOpen(string site, IEnumerable<string> openAddresses) => openAddresses.Any(a => Belongs(a, site));

    // O que a confirmação de «Triturar» diz que vai apagar.
    public static string ShredSummary(string site, int cookies, int history)
    {
        var parts = new List<string>
        {
            cookies == 0 ? "nenhum cookie" : cookies == 1 ? "1 cookie" : $"{cookies} cookies",
            history == 0 ? "nenhuma entrada do histórico" : history == 1 ? "1 entrada do histórico" : $"{history} entradas do histórico",
            "o armazenamento do site (local, bases de dados, cache e trabalhadores)",
            "as permissões dadas",
        };
        // Um endereço IP não tem subdomínios.
        var scope = System.Net.IPAddress.TryParse(site, out _) ? site : $"{site} e dos seus subdomínios";
        return $"Vai apagar de {scope}: {string.Join(", ", parts.Take(parts.Count - 1))} e {parts[^1]}. "
               + "As abas deste site nesta janela fecham. Não dá para anular.";
    }
}
