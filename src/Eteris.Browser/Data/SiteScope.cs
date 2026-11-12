namespace Eteris.Browser.Data;

// O «site» de «Esquecer este site ao fechar» e de «Triturar»: o domínio registável (eTLD+1) pela Public Suffix List
// (Shields/PublicSuffix.cs) — exemplo.pt cobre www.exemplo.pt e loja.exemplo.pt, como os cookies de .exemplo.pt — e
// nunca outro site: nem um com nome parecido (malexemplo.pt não é exemplo.pt) nem um vizinho num sufixo partilhado
// (alice.github.io não é bob.github.io). Só páginas da web (http/https); um IP ou localhost é o próprio site.
public static class SiteScope
{
    public static string? SiteOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? OfHost(uri.Host)
            : null;

    public static string OfHost(string host) => Shields.PublicSuffix.RegistrableDomain(host.Trim('.').ToLowerInvariant());

    public static bool Belongs(string? url, string site) => SiteOf(url) is { } own && own == site;

    // Domínio de um cookie (".exemplo.pt", "www.exemplo.pt") dentro do site: o site do domínio é o próprio site (um
    // cookie de .alice.github.io não é de github.io).
    public static bool CookieBelongs(string cookieDomain, string site)
    {
        var host = cookieDomain.Trim().TrimStart('.').ToLowerInvariant();
        return host.Length > 0 && (host == site || host.EndsWith("." + site, StringComparison.Ordinal)) && OfHost(host) == site;
    }

    // Chave de uma permissão («meet.google.com», «localhost:3000», «[::1]:8080») dentro do site, com qualquer porta.
    public static bool HostKeyBelongs(string key, string site)
        => Uri.TryCreate("http://" + key.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 && OfHost(uri.Host) == site;

    // Endereço para apagar um cookie pelo motor (o CookieManager apaga por URL + nome).
    public static string CookieUrl(string cookieDomain, string path)
        => "https://" + cookieDomain.Trim().TrimStart('.') + (path.StartsWith('/') ? path : "/" + path);

    // Os cookies a apagar de um site (o domínio e os subdomínios, pela mesma regra do Triturar), pela ordem dada: o
    // endereço para o CookieManager, o nome e o anfitrião (sem o ponto inicial, em minúsculas).
    public static IReadOnlyList<(string Url, string Name, string Host)> CookieDeletions(IEnumerable<(string Domain, string Path, string Name)> cookies, string site)
        => cookies.Where(c => CookieBelongs(c.Domain, site))
            .Select(c => (CookieUrl(c.Domain, c.Path), c.Name, c.Domain.Trim().TrimStart('.').ToLowerInvariant()))
            .ToList();

    // As origens (esquema://host[:porta]) do site vistas nos endereços dados — abas abertas, histórico — mais as duas
    // óbvias (com e sem www, em https e http): é por origem que o armazenamento se apaga. Um IP ou um nome sem ponto
    // (localhost) não tem «www.»: essa origem nem é válida para o motor.
    public static IReadOnlyList<string> Origins(string site, IEnumerable<string> seenUrls)
    {
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        var bare = System.Net.IPAddress.TryParse(site.Trim('[', ']'), out _) || !site.Contains('.');
        foreach (var host in bare ? new[] { site } : new[] { site, "www." + site })
            foreach (var scheme in new[] { "https", "http" })
                origins.Add($"{scheme}://{host}");
        foreach (var url in seenUrls)
        {
            if (!Belongs(url, site) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            origins.Add(uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}" : $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port}");
        }
        return origins.ToList();
    }

    // A origem (esquema://host[:porta], em minúsculas, sem a porta por omissão) de um endereço da web; nula para o
    // resto (eteris://, about:, ficheiros).
    public static string? OriginOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        var host = uri.Host.ToLowerInvariant();
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    // «Apagar dados › Cookies e dados de sites»: as origens cujo armazenamento se apaga — as do histórico, as das abas
    // abertas e, de cada domínio de cookie, a origem https e a http. Sem repetidas, por ordem.
    public static IReadOnlyList<string> KnownOrigins(IEnumerable<string> historyUrls, IEnumerable<string> cookieDomains, IEnumerable<string> openAddresses)
    {
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var url in historyUrls.Concat(openAddresses))
            if (OriginOf(url) is { } origin) origins.Add(origin);
        foreach (var domain in cookieDomains)
        {
            var host = domain.Trim().TrimStart('.').ToLowerInvariant();
            if (host.Length == 0) continue;
            foreach (var scheme in new[] { "https", "http" })
                if (OriginOf($"{scheme}://{host}/") is { } origin) origins.Add(origin);
        }
        return origins.ToList();
    }

    // Fechou-se uma aba: ainda há alguma do site em qualquer janela (a dormir, noutros Espaços)? Só sem nenhuma é que
    // «Esquecer ao fechar» apaga.
    public static bool StillOpen(string site, IEnumerable<string> openAddresses) => openAddresses.Any(a => Belongs(a, site));

    // Os sites com «esquecer ao fechar» que ficam sem nenhuma aba quando estas saem (fechar a janela, apagar um Espaço,
    // navegar para outro site, sair do Éteris): remaining é tudo o que continua aberto.
    public static IReadOnlyList<string> SitesLeaving(IEnumerable<string?> leaving, IEnumerable<string> remaining, Func<string, bool> isForget)
    {
        var open = remaining.ToList();
        return leaving.Select(SiteOf).OfType<string>().Distinct(StringComparer.Ordinal)
            .Where(site => isForget(site) && !StillOpen(site, open)).ToList();
    }

    // O que a confirmação de «Triturar» diz que vai apagar. Na janela privada não há histórico, e as permissões são as
    // dadas nessa janela. closingTabs: as abas do site nesta janela (que fecham); locked/noted: quantas delas estão
    // trancadas ou têm nota, que se perdem com elas.
    public static string ShredSummary(string site, int cookies, int history, bool isPrivate = false, int closingTabs = -1, int locked = 0, int noted = 0)
    {
        var parts = new List<string> { cookies == 0 ? "nenhum cookie" : cookies == 1 ? "1 cookie" : $"{cookies} cookies" };
        if (!isPrivate)
            parts.Add(history == 0 ? "nenhuma entrada do histórico" : history == 1 ? "1 entrada do histórico" : $"{history} entradas do histórico");
        parts.Add("o armazenamento do site (local, bases de dados, cache e trabalhadores)");
        parts.Add(isPrivate ? "as permissões dadas nesta janela" : "as permissões dadas");
        // Um endereço IP (ou localhost) não tem subdomínios.
        var bare = System.Net.IPAddress.TryParse(site.Trim('[', ']'), out _) || !site.Contains('.');
        var scope = bare ? site : $"{site} e dos seus subdomínios";
        return $"Vai apagar de {scope}: {string.Join(", ", parts.Take(parts.Count - 1))} e {parts[^1]}. "
               + TabsLine(closingTabs, locked, noted) + "Não dá para anular.";
    }

    private static string TabsLine(int tabs, int locked, int noted)
    {
        if (tabs < 0) return "As abas deste site nesta janela fecham. ";
        if (tabs == 0) return string.Empty;
        var head = tabs == 1 ? "A aba deste site nesta janela fecha" : $"As {tabs} abas deste site nesta janela fecham";
        var lost = new List<string>();
        if (locked > 0) lost.Add(locked == 1 ? "1 trancada" : $"{locked} trancadas");
        if (noted > 0) lost.Add(noted == 1 ? "1 com nota" : $"{noted} com nota");
        return lost.Count == 0 ? head + ". " : $"{head}, incluindo {string.Join(" e ", lost)} (a nota e o nome perdem-se). ";
    }

    // Bytes do localStorage numa resposta de DOMStorage.getDOMStorageItems ({"entries":[["chave","valor"],…]}): chave
    // e valor em UTF-16, como o Chromium os conta.
    public static long LocalStorageBytes(System.Text.Json.JsonElement response)
    {
        if (response.ValueKind != System.Text.Json.JsonValueKind.Object
            || !response.TryGetProperty("entries", out var entries) || entries.ValueKind != System.Text.Json.JsonValueKind.Array) return 0;
        long total = 0;
        foreach (var entry in entries.EnumerateArray())
            if (entry.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var text in entry.EnumerateArray())
                    if (text.ValueKind == System.Text.Json.JsonValueKind.String) total += 2L * (text.GetString()?.Length ?? 0);
        return total;
    }

    // O aviso depois de apagar. storage: o armazenamento foi mesmo apagado (precisa de uma página viva do contexto).
    public static string ShredDone(int cookies, int history, bool storage, bool isPrivate = false)
    {
        var cookieText = cookies == 1 ? "1 cookie" : $"{cookies} cookies";
        var text = isPrivate
            ? cookieText + (cookies == 1 ? " apagado" : " apagados")
            : $"{cookieText} e {(history == 1 ? "1 entrada do histórico" : $"{history} entradas do histórico")} apagados";
        return storage ? text : text + " — o armazenamento ficou por apagar (o motor não estava pronto)";
    }

    // O aviso do cadeado › «Apagar cookies e dados deste site»: quantos cookies saíram e, se o armazenamento não
    // pôde ser apagado, di-lo.
    public static string SiteDataCleared(string site, int cookies, bool storage)
    {
        var cookieText = cookies == 0 ? "nenhum cookie" : cookies == 1 ? "1 cookie" : $"{cookies} cookies";
        return $"Dados de {site} apagados ({cookieText})" + (storage ? "" : " — o armazenamento ficou por apagar");
    }
}
