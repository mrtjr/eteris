using System.Globalization;
using System.Net;

namespace Eteris.Browser.Vault;

public enum OriginMatch
{
    None,
    // A mesma origem (esquema, anfitrião e porta).
    Exact,
    // Outro anfitrião do mesmo domínio registável, com o mesmo esquema e a mesma porta: sugere-se marcado
    // («de conta.exemplo.com»), depois das exatas.
    SameSite,
}

// A origem de uma credencial (docs/palavras-passe.md): esquema + anfitrião (IDN em ASCII, minúsculas) + porta, sem
// caminho nem utilizador. Só http e https se sugerem; android:// (exportado pelo Chrome) guarda-se tal como vem e nunca se
// sugere. A origem de uma página vem sempre do processo do navegador, nunca do que a página diz.
public static class Origins
{
    private const string Android = "android://";

    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.StartsWith(Android, StringComparison.OrdinalIgnoreCase))
            return url.Length > Android.Length && url.IndexOfAny([' ', '\t', '\r', '\n']) < 0 ? Android + url[Android.Length..] : null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        string host;
        try { host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost; }
        catch (UriFormatException) { return null; }
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return null;
        return uri.Scheme + "://" + host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
    }

    public static bool IsAndroid(string origin) => origin.StartsWith(Android, StringComparison.OrdinalIgnoreCase);

    // Onde se preenchem cartões, IBAN e documentos: https, e o próprio computador (localhost, 127.0.0.1, [::1]).
    public static bool IsSecure(string origin)
    {
        if (Normalize(origin) is not { } normal || IsAndroid(normal)) return false;
        var uri = new Uri(normal);
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        var host = uri.Host.Trim('[', ']');
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    // Exata → mesmo domínio registável (Public Suffix List, como o Chromium) → nunca entre domínios. http e https
    // separados; portas iguais; um IP só corresponde a si próprio; android:// nunca.
    public static OriginMatch Match(string saved, string current)
    {
        if (Normalize(saved) is not { } a || Normalize(current) is not { } b || IsAndroid(a) || IsAndroid(b)) return OriginMatch.None;
        if (a == b) return OriginMatch.Exact;
        Uri ua = new(a), ub = new(b);
        if (ua.Scheme != ub.Scheme || ua.Port != ub.Port) return OriginMatch.None;
        if (ua.HostNameType != UriHostNameType.Dns || ub.HostNameType != UriHostNameType.Dns) return OriginMatch.None;
        var site = Shields.PublicSuffix.RegistrableDomain(ua.Host);
        return site.Contains('.') && site == Shields.PublicSuffix.RegistrableDomain(ub.Host) ? OriginMatch.SameSite : OriginMatch.None;
    }

    // O domínio registável (o segundo índice do cofre e o agrupamento por site na lista); null para android://.
    public static string? Site(string origin)
        => Normalize(origin) is { } normal && !IsAndroid(normal) ? Shields.PublicSuffix.RegistrableDomain(new Uri(normal).Host) : null;

    public static string? Host(string origin)
        => Normalize(origin) is { } normal && !IsAndroid(normal) ? new Uri(normal).Host : null;

    // O anfitrião como o utilizador o lê (IDN em Unicode).
    public static string? DisplayHost(string origin)
    {
        if (Host(origin) is not { } host) return null;
        try { return new IdnMapping().GetUnicode(host); }
        catch (ArgumentException) { return host; }
    }
}
