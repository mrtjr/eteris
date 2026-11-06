using System.IO;

namespace Eteris.Browser.Shields;

public interface IRequestBlocker
{
    bool ShouldBlock(string requestUrl, string? pageUrl);
}

// Bloqueio por domínio, provisório até ao motor adblock-rust; quem o usa depende só de IRequestBlocker.
public sealed class TrackerBlocker : IRequestBlocker
{
    private static readonly HashSet<string> SecondLevelLabels = ["co", "com", "org", "net", "gov", "edu", "ac", "gob", "nom"];

    private static readonly Lazy<TrackerBlocker> LazyDefault = new(() =>
    {
        using var stream = typeof(TrackerBlocker).Assembly.GetManifestResourceStream("Eteris.Browser.Shields.trackers.txt")!;
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'));
        return new TrackerBlocker(lines);
    });

    private readonly HashSet<string> _domains;

    public TrackerBlocker(IEnumerable<string> domains)
        => _domains = new HashSet<string>(domains.Select(d => d.ToLowerInvariant()), StringComparer.Ordinal);

    public static TrackerBlocker Default => LazyDefault.Value;

    public int Count => _domains.Count;

    public bool ShouldBlock(string requestUrl, string? pageUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)) return false;
        if (request.Scheme != Uri.UriSchemeHttp && request.Scheme != Uri.UriSchemeHttps) return false;

        var host = request.Host.ToLowerInvariant();
        if (!IsListed(host)) return false;

        if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && page.Host.Length > 0)
            return RegistrableDomain(page.Host.ToLowerInvariant()) != RegistrableDomain(host);
        return true;
    }

    // Aproximação do "eTLD+1" sem a Public Suffix List: cobre .co.uk, .com.br, .com.pt, etc.
    public static string RegistrableDomain(string host)
    {
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var take = labels[^1].Length == 2 && SecondLevelLabels.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    private bool IsListed(string host)
    {
        for (var candidate = host; ; )
        {
            if (_domains.Contains(candidate)) return true;
            var dot = candidate.IndexOf('.');
            if (dot < 0) return false;
            candidate = candidate[(dot + 1)..];
        }
    }
}
