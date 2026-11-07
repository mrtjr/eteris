using Eteris.Browser.Data;

namespace Eteris.Browser.Shields;

// Domínios que o utilizador bloqueou só num site, a partir do Observatório («Bloquear neste site»). A regra cobre o
// domínio e os seus subdomínios, só quando a página de topo é desse site (domínio registável). Consultada pela thread
// de rede antes das listas: sem regras nenhumas, a consulta é uma leitura de um número.
public sealed class SiteBlockRules
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, HashSet<string>> _rules = new(StringComparer.OrdinalIgnoreCase);
    private volatile int _count;

    public bool IsEmpty => _count == 0;

    public bool Add(string site, string domain)
    {
        lock (_lock)
        {
            if (!_rules.TryGetValue(site, out var set)) _rules[site] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = set.Add(Normalise(domain));
            if (added) _count++;
            return added;
        }
    }

    public bool Remove(string site, string domain)
    {
        lock (_lock)
        {
            if (!_rules.TryGetValue(site, out var set) || !set.Remove(Normalise(domain))) return false;
            if (set.Count == 0) _rules.Remove(site);
            _count--;
            return true;
        }
    }

    public void RemoveSite(string site)
    {
        lock (_lock)
        {
            if (_rules.Remove(site, out var set)) _count -= set.Count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _rules.Clear();
            _count = 0;
        }
    }

    public IReadOnlyList<string> ForSite(string site)
    {
        lock (_lock) return _rules.TryGetValue(site, out var set) ? set.Order(StringComparer.Ordinal).ToList() : [];
    }

    public IReadOnlyList<(string Site, string Domain)> All()
    {
        lock (_lock) return _rules.SelectMany(p => p.Value.Select(d => (p.Key, d))).OrderBy(r => r.Key).ThenBy(r => r.d).ToList();
    }

    // O anfitrião é o domínio da regra ou um subdomínio dele (ads.tracker.example cai em tracker.example).
    public bool Blocks(string site, string host)
    {
        if (_count == 0) return false;
        lock (_lock)
        {
            if (!_rules.TryGetValue(site, out var set)) return false;
            for (var candidate = host.TrimEnd('.'); candidate.Length > 0;)
            {
                if (set.Contains(candidate)) return true;
                var dot = candidate.IndexOf('.');
                if (dot < 0) return false;
                candidate = candidate[(dot + 1)..];
            }
            return false;
        }
    }

    private static string Normalise(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();
}

// As regras em vigor: as guardadas (janelas normais, base de dados) e as da sessão privada (só em memória, esquecidas
// quando a última janela privada fecha). Uma regra feita numa janela normal vale também nas privadas.
public static class SiteRules
{
    private static readonly Lazy<SiteRuleStore?> Store = new(() =>
    {
        try { return new SiteRuleStore(Infrastructure.AppPaths.Database); }
        catch (Exception) { return null; }
    });

    private static readonly Lazy<SiteBlockRules> Saved = new(() =>
    {
        var rules = new SiteBlockRules();
        try
        {
            foreach (var (site, domain) in Store.Value?.All() ?? []) rules.Add(site, domain);
        }
        catch (Exception) { }
        return rules;
    });

    private static readonly SiteBlockRules Private = new();

    // Decisão por pedido: o site da página de topo e o anfitrião do pedido.
    public static bool Blocks(string? pageUrl, string requestUrl, bool isPrivate)
    {
        var saved = Saved.Value;
        if (saved.IsEmpty && (!isPrivate || Private.IsEmpty)) return false;
        if (SiteScope.SiteOf(pageUrl) is not { } site) return false;
        var host = SiteObservatory.HostOf(requestUrl).ToString();
        if (host.Length == 0) return false;
        return saved.Blocks(site, host) || (isPrivate && Private.Blocks(site, host));
    }

    public static bool IsBlocked(string site, string domain, bool isPrivate)
        => Saved.Value.ForSite(site).Contains(domain, StringComparer.OrdinalIgnoreCase)
           || (isPrivate && Private.ForSite(site).Contains(domain, StringComparer.OrdinalIgnoreCase));

    // Na janela privada nada fica no disco.
    public static void Block(string site, string domain, bool isPrivate)
    {
        if (isPrivate)
        {
            Private.Add(site, domain);
            return;
        }
        if (!Saved.Value.Add(site, domain)) return;
        try { Store.Value?.Add(site, domain); }
        catch (Exception) { }
    }

    public static void Unblock(string site, string domain, bool isPrivate)
    {
        Private.Remove(site, domain);
        if (isPrivate) return;
        Saved.Value.Remove(site, domain);
        try { Store.Value?.Remove(site, domain); }
        catch (Exception) { }
    }

    // «Repor» o site: as regras dele saem.
    public static void Forget(string site)
    {
        Private.RemoveSite(site);
        Saved.Value.RemoveSite(site);
        try { Store.Value?.RemoveSite(site); }
        catch (Exception) { }
    }

    public static void ForgetPrivate() => Private.Clear();

    public static IReadOnlyList<(string Site, string Domain)> All() => Saved.Value.All();
}
