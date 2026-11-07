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
