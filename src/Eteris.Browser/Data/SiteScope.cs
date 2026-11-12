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
        // cookies < 0: o motor não os deixou contar (não se diz «nenhum»).
        var parts = new List<string> { cookies < 0 ? "os cookies (não foi possível contá-los)" : cookies == 0 ? "nenhum cookie" : cookies == 1 ? "1 cookie" : $"{cookies} cookies" };
        if (!isPrivate)
            parts.Add(history == 0 ? "nenhuma entrada do histórico" : history == 1 ? "1 entrada do histórico" : $"{history} entradas do histórico");
        parts.Add("o armazenamento do site (local, bases de dados, cache e trabalhadores)");
        // As listas do Radar (Feitas, Arquivo, Adiadas) são do perfil: só a janela normal as tem.
        if (!isPrivate) parts.Add("as abas feitas, arquivadas e adiadas deste site");
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
    // cookiesFailed: o motor não deixou ler ou apagar os cookies; videoEngineFailed: o perfil do motor de vídeo existe
    // e não se abriu. Só o armazenamento em falta mantém a frase de sempre; o resto diz «não foi possível apagar».
    public static string ShredDone(int cookies, int history, bool storage, bool isPrivate = false, bool cookiesFailed = false, bool videoEngineFailed = false)
    {
        var cookieText = cookies == 1 ? "1 cookie" : $"{cookies} cookies";
        var text = isPrivate
            ? cookieText + (cookies == 1 ? " apagado" : " apagados")
            : $"{cookieText} e {(history == 1 ? "1 entrada do histórico" : $"{history} entradas do histórico")} apagados";
        if (!cookiesFailed && !videoEngineFailed)
            return storage ? text : text + " — o armazenamento ficou por apagar (o motor não estava pronto)";
        var stayed = new List<string>();
        if (cookiesFailed) stayed.Add("todos os cookies");
        if (!storage) stayed.Add("o armazenamento");
        if (videoEngineFailed) stayed.Add("os dados do motor de vídeo");
        var names = stayed.Count == 1 ? stayed[0] : string.Join(", ", stayed[..^1]) + " e " + stayed[^1];
        return text + " — não foi possível apagar " + names;
    }

    // O resultado da varredura do arranque para um site (MainWindow.Forget.cs, ForgetLeftoversAsync).
    public sealed record Leftover(string Site, int Cookies, int History, bool Storage, bool CookiesFailed = false, bool VideoEngineFailed = false)
    {
        // O armazenamento por fazer só conta num site de que se apagou alguma coisa: sem página viva do motor principal
        // (a aba ativa no motor de vídeo, abas a dormir) falhava em todos os sites, em cada arranque, sem nada que se
        // saiba ter ficado.
        public bool Failed => CookiesFailed || VideoEngineFailed || (!Storage && Cookies + History > 0);
    }

    // O aviso da varredura do arranque: os sites esquecidos e, primeiro, os que ficaram com alguma coisa por apagar (o
    // aviso começa então por «Não foi»). Nada apagado e nada falhado: sem aviso (é o caso de quase todos os arranques).
    public static string? LeftoversDone(IReadOnlyList<Leftover> results)
    {
        var failed = results.Where(r => r.Failed).ToList();
        var forgotten = results.Where(r => !r.Failed && r.Cookies + r.History > 0).Select(r => r.Site).ToList();
        if (failed.Count == 0)
        {
            if (forgotten.Count == 0) return null;
            return forgotten.Count == 1 ? $"{forgotten[0]} foi esquecido (tinha ficado por apagar ao sair)"
                : $"{forgotten.Count} sites esquecidos (tinham ficado por apagar ao sair): {string.Join(", ", forgotten)}";
        }
        if (failed.Count == 1 && forgotten.Count == 0)
        {
            var r = failed[0];
            return $"Não foi possível esquecer tudo de {r.Site} (tinha ficado por apagar ao sair) — "
                   + ShredDone(r.Cookies, r.History, r.Storage, false, r.CookiesFailed, r.VideoEngineFailed);
        }
        var text = $"Não foi possível esquecer tudo de {Join(failed.Select(r => r.Site).ToList())} ({(failed.Count == 1 ? "tinha" : "tinham")} ficado por apagar ao sair)";
        return forgotten.Count == 0 ? text : text + $"; {Join(forgotten)} {(forgotten.Count == 1 ? "foi esquecido" : "foram esquecidos")}";
    }

    private static string Join(List<string> names) => names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " e " + names[^1];

    // O aviso do cadeado › «Apagar cookies e dados deste site»: quantos cookies saíram e, se o armazenamento não
    // pôde ser apagado, di-lo.
    // cookiesFailed: o motor recusou apagar algum cookie; videoEngineFailed: o perfil do motor de vídeo não se abriu ou
    // recusou alguma origem. Uma falha começa por «Não foi» e diz o que ficou.
    public static string SiteDataCleared(string site, int cookies, bool storage, bool cookiesFailed = false, bool videoEngineFailed = false)
    {
        var cookieText = cookies == 0 ? "nenhum cookie" : cookies == 1 ? "1 cookie" : $"{cookies} cookies";
        if (storage && !cookiesFailed && !videoEngineFailed) return $"Dados de {site} apagados ({cookieText})";
        var stayed = new List<string>();
        if (cookiesFailed) stayed.Add("todos os cookies");
        if (!storage) stayed.Add("o armazenamento");
        if (videoEngineFailed) stayed.Add("os dados do motor de vídeo");
        return $"Não foi possível apagar {Join(stayed)} de {site} ({cookieText} {(cookies > 1 ? "apagados" : "apagado")})";
    }
}
