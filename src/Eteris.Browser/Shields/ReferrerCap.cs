namespace Eteris.Browser.Shields;

// Referrer entre sites só com a origem (como o Brave): um site que vê o Éteris chegar de outro fica a saber de que
// site vem, nunca de que página (o artigo, a pesquisa, o identificador no endereço). Vale mesmo que a página peça
// mais (Referrer-Policy: unsafe-url, <meta name="referrer">). Dentro do mesmo site nada muda.
public static class ReferrerCap
{
    public enum Kind { Keep, Origin, Strip }

    public readonly record struct Decision(Kind Kind, string? Value);

    public static Decision Decide(string? referrer, string requestUrl)
    {
        if (string.IsNullOrEmpty(referrer) || !Uri.TryCreate(referrer, UriKind.Absolute, out var from)
            || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var to)
            || !IsWeb(from) || !IsWeb(to) || SameSite(from, to))
            return new(Kind.Keep, null);
        // De https para http não vai nada (como a política por omissão do Chromium, strict-origin).
        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttp) return new(Kind.Strip, null);
        var origin = from.GetLeftPart(UriPartial.Authority) + "/";
        return string.Equals(origin, referrer, StringComparison.Ordinal) ? new(Kind.Keep, null) : new(Kind.Origin, origin);
    }

    // «Mesmo site» como no Chromium (schemeful same-site): o mesmo esquema e o mesmo domínio registável.
    public static bool SameSite(Uri a, Uri b)
        => a.Scheme == b.Scheme && Data.SiteScope.OfHost(a.Host) == Data.SiteScope.OfHost(b.Host);

    private static bool IsWeb(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}

// O documento de topo para as decisões de «mesmo site» (cookies de terceiros, rede local, listas): um blob: ou um
// filesystem: é da origem que o criou (blob:https://evil.example/… é de https://evil.example) — senão bastava a uma
// página navegar para um URL blob: para deixar de ser «um site». O resto fica como está (about:blank, data:, as
// páginas internas): quem decide trata-o como um documento sem origem web.
public static class TopDocument
{
    public static string? Effective(string? url)
    {
        if (url is null) return null;
        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return url["blob:".Length..];
        if (url.StartsWith("filesystem:", StringComparison.OrdinalIgnoreCase)) return url["filesystem:".Length..];
        return url;
    }

    // O site (esquema e eTLD+1, schemeful como no Chromium) de um documento http(s); null sem origem web.
    public static string? SiteKey(string? url)
        => Uri.TryCreate(Effective(url), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri.Scheme + "://" + Data.SiteScope.OfHost(uri.Host)
            : null;
}

// Cookies de terceiros (Definições › «Bloquear cookies de terceiros»): só o site de topo envia e grava cookies.
// Uma navegação de topo é sempre primeira parte; um site com o escudo desligado fica de fora. Um documento sem origem
// web (about:blank, data:, uma página interna, um blob: opaco) não é o site de ninguém: os pedidos http(s) dele são de
// terceiros. Os cookies «Partitioned» (CHIPS) de um terceiro passam à parte (PartitionedCookieJar).
public static class ThirdPartyCookies
{
    public static bool Allow(string? topUrl, string requestUrl, CefSharp.ResourceType type, bool block, bool shieldOff = false)
    {
        if (!block || shieldOff || type == CefSharp.ResourceType.MainFrame) return true;
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) || request.Scheme is not ("http" or "https")) return true;
        if (!Uri.TryCreate(TopDocument.Effective(topUrl), UriKind.Absolute, out var top) || top.Scheme is not ("http" or "https")) return false;
        return ReferrerCap.SameSite(top, request);
    }

    // O cookie com este nome veio com o atributo Partitioned num dos Set-Cookie da resposta (CHIPS: guardado só no
    // frasco do site de topo). O CefSharp não diz se um cookie é particionado; o cabeçalho diz.
    public static bool IsPartitioned(IEnumerable<string>? setCookie, string name)
    {
        if (setCookie is null) return false;
        foreach (var line in setCookie)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var parts = line.Split(';');
            var eq = parts[0].IndexOf('=');
            var cookieName = (eq < 0 ? string.Empty : parts[0][..eq]).Trim();
            if (!string.Equals(cookieName, name, StringComparison.Ordinal)) continue;
            var partitioned = false;
            var secure = false;
            foreach (var attribute in parts.Skip(1))
            {
                var key = attribute.Split('=', 2)[0].Trim();
                if (key.Equals("Partitioned", StringComparison.OrdinalIgnoreCase)) partitioned = true;
                else if (key.Equals("Secure", StringComparison.OrdinalIgnoreCase)) secure = true;
            }
            // Sem Secure o Chromium recusa o Partitioned: não é um cookie particionado.
            if (partitioned && secure) return true;
        }
        return false;
    }
}

// Os cookies «Partitioned» (CHIPS) que um terceiro gravou embutido num site: o Chromium guarda-os no frasco desse site
// de topo (não servem noutro site), por isso o filtro deixa-os gravar e voltar a ser enviados — só no mesmo site de
// topo e só com o valor que foi gravado como particionado (um cookie normal do terceiro com o mesmo nome não passa
// por arrasto). Só em memória: depois de reiniciar o Éteris o terceiro volta a pedi-lo (como um cookie de sessão).
public sealed class PartitionedCookieJar
{
    private const int Limit = 4096;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Top, string Domain, string Name, string Path), string> _cookies = new();

    public static PartitionedCookieJar Normal { get; } = new();
    public static PartitionedCookieJar Private { get; } = new();

    public static PartitionedCookieJar For(bool isPrivate) => isPrivate ? Private : Normal;

    private static (string, string, string, string) Key(string topSite, string domain, string name, string path)
        => (topSite, domain.TrimStart('.').ToLowerInvariant(), name, string.IsNullOrEmpty(path) ? "/" : path);

    public void Remember(string topSite, string domain, string name, string path, string value)
    {
        if (_cookies.Count >= Limit) _cookies.Clear();
        _cookies[Key(topSite, domain, name, path)] = value;
    }

    public bool Holds(string topSite, string domain, string name, string path, string value)
        => _cookies.TryGetValue(Key(topSite, domain, name, path), out var saved) && string.Equals(saved, value, StringComparison.Ordinal);

    public void Clear() => _cookies.Clear();
}
