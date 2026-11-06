namespace Eteris.Browser.Tabs;

public static class SiteBadge
{
    public const int PaletteSize = 6;

    public static string? Letter(string address)
    {
        var host = Host(address);
        return host is { Length: > 0 } ? host[..1].ToUpperInvariant() : null;
    }

    // FNV-1a: string.GetHashCode é aleatório por processo e a cor de um site tem de ser sempre a mesma.
    public static int ColorIndex(string address)
    {
        var host = Host(address) ?? string.Empty;
        var hash = 2166136261u;
        foreach (var c in host)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return (int)(hash % PaletteSize);
    }

    private static string? Host(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.IdnHost.Length > 0 ? uri.Host.ToLowerInvariant() : null;
        return host is not null && host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
