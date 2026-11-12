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

    public bool Contains(string site, string domain)
    {
        if (_count == 0) return false;
        lock (_lock) return _rules.TryGetValue(site, out var set) && set.Contains(Normalise(domain));
    }

    // O anfitrião é o domínio da regra ou um subdomínio dele (ads.tracker.example cai em tracker.example).
    public bool Blocks(string site, string host)
    {
        if (_count == 0) return false;
        lock (_lock)
        {
            if (!_rules.TryGetValue(site, out var set)) return false;
            foreach (var candidate in Candidates(host))
                if (set.Contains(candidate)) return true;
            return false;
        }
    }

    // As regras que apanham o anfitrião, da mais estreita à mais larga (cdn.tracker.example, depois tracker.example).
    public IReadOnlyList<string> RulesFor(string site, string host)
    {
        if (_count == 0) return [];
        lock (_lock)
        {
            if (!_rules.TryGetValue(site, out var set)) return [];
            return Candidates(host).Where(set.Contains).ToList();
        }
    }

    private static IEnumerable<string> Candidates(string host)
    {
        for (var candidate = Normalise(host); candidate.Length > 0;)
        {
            yield return candidate;
            var dot = candidate.IndexOf('.');
            if (dot < 0) yield break;
            candidate = candidate[(dot + 1)..];
        }
    }

    private static string Normalise(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();
}

// O que «Deixar de bloquear» desfez: as regras que deixaram de valer (da mais estreita à mais larga) e se alguma era
// uma regra guardada que, numa janela privada, fica só suspensa até a sessão privada acabar.
public sealed record SiteUnblock(IReadOnlyList<string> Rules, bool SavedKept);

// As regras em vigor: as guardadas (janelas normais) e as da sessão privada (só em memória, esquecidas quando a última
// janela privada fecha). Uma regra feita numa janela normal vale também nas privadas; «Deixar de bloquear» numa janela
// privada suspende-a só nessa sessão (uma exceção em memória), sem a apagar do disco. Desbloquear um anfitrião desfaz
// todas as regras que o apanham, também a do domínio-pai: o botão nunca fica sem efeito.
public sealed class SiteRuleSet(SiteBlockRules saved, Action<string, string>? persistAdd = null,
    Action<string, string>? persistRemove = null, Action<string>? persistRemoveSite = null)
{
    private readonly SiteBlockRules _private = new();
    private readonly SiteBlockRules _suspended = new();

    public bool IsEmpty(bool isPrivate) => saved.IsEmpty && (!isPrivate || _private.IsEmpty);

    public bool Blocks(string site, string host, bool isPrivate)
    {
        if (!isPrivate) return saved.Blocks(site, host);
        if (_private.Blocks(site, host)) return true;
        if (_suspended.IsEmpty) return saved.Blocks(site, host);
        return saved.RulesFor(site, host).Any(rule => !_suspended.Contains(site, rule));
    }

    // Na janela privada nada fica no disco; uma regra guardada suspensa nesta sessão volta a valer.
    public void Block(string site, string domain, bool isPrivate)
    {
        if (isPrivate)
        {
            var resumed = _suspended.Remove(site, domain);
            if (!resumed || !saved.Contains(site, domain)) _private.Add(site, domain);
            return;
        }
        if (saved.Add(site, domain)) persistAdd?.Invoke(site, domain);
    }

    public SiteUnblock Unblock(string site, string host, bool isPrivate)
    {
        var undone = new List<string>();
        var savedKept = false;
        if (isPrivate)
            foreach (var rule in _private.RulesFor(site, host))
                if (_private.Remove(site, rule)) undone.Add(rule);
        foreach (var rule in saved.RulesFor(site, host))
        {
            if (isPrivate)
            {
                if (!_suspended.Add(site, rule)) continue;
                savedKept = true;
            }
            else
            {
                saved.Remove(site, rule);
                persistRemove?.Invoke(site, rule);
            }
            if (!undone.Contains(rule, StringComparer.OrdinalIgnoreCase)) undone.Add(rule);
        }
        return new SiteUnblock(undone, savedKept);
    }

    // A sincronização tira exatamente esta regra (Unblock tira todas as que apanham o anfitrião, também a do pai).
    public void RemoveExact(string site, string domain)
    {
        if (saved.Remove(site, domain)) persistRemove?.Invoke(site, domain);
    }

    // «Repor» o site: as regras dele saem.
    public void Forget(string site)
    {
        _private.RemoveSite(site);
        _suspended.RemoveSite(site);
        saved.RemoveSite(site);
        persistRemoveSite?.Invoke(site);
    }

    public void ForgetPrivate()
    {
        _private.Clear();
        _suspended.Clear();
    }

    public IReadOnlyList<(string Site, string Domain)> All() => saved.All();
}

// As regras do Éteris: as guardadas vêm da base de dados (Data/SiteRuleStore) e cada alteração feita numa janela
// normal vai para lá.
public static class SiteRules
{
    private static readonly Lazy<SiteRuleStore?> Store = new(() =>
    {
        try { return new SiteRuleStore(Infrastructure.AppPaths.Database); }
        catch (Exception) { return null; }
    });

    // O que a carga das regras guardadas deu: uma carga que falhou (a base não abre, ou lança) não é «sem regras», e a
    // sincronização sabe-o (ReadAll): uma lista que não se leu não se envia como vazia.
    private static volatile bool _loadFailed;

    // As regras guardadas, lidas por «all». null ou uma exceção = não se leu (Failed): a lista fica vazia mas marcada.
    internal static (SiteBlockRules Saved, bool Failed) Load(Func<IReadOnlyList<(string Site, string Domain)>?> all)
    {
        var saved = new SiteBlockRules();
        try
        {
            if (all() is not { } rules) return (saved, true);
            foreach (var (site, domain) in rules) saved.Add(site, domain);
            return (saved, false);
        }
        catch (Exception) { return (saved, true); }
    }

    private static readonly Lazy<SiteRuleSet> Rules = new(() =>
    {
        var (saved, failed) = Load(() => Store.Value?.All());
        _loadFailed = failed;
        return new SiteRuleSet(saved,
            (site, domain) => Quietly(() => Store.Value?.Add(site, domain)),
            (site, domain) => Quietly(() => Store.Value?.Remove(site, domain)),
            site => Quietly(() => Store.Value?.RemoveSite(site)));
    });

    private static void Quietly(Action write)
    {
        try { write(); }
        catch (Exception) { }
    }

    // Decisão por pedido: o site da página de topo e o anfitrião do pedido.
    public static bool Blocks(string? pageUrl, string requestUrl, bool isPrivate)
    {
        var rules = Rules.Value;
        if (rules.IsEmpty(isPrivate)) return false;
        if (SiteScope.SiteOf(pageUrl) is not { } site) return false;
        var host = SiteObservatory.HostOf(requestUrl).ToString();
        if (host.Length == 0) return false;
        return rules.Blocks(site, host, isPrivate);
    }

    public static void Block(string site, string domain, bool isPrivate)
    {
        Rules.Value.Block(site, domain, isPrivate);
        if (!isPrivate) Data.AppData.NotifySyncableChanged();
    }

    public static SiteUnblock Unblock(string site, string host, bool isPrivate)
    {
        var undone = Rules.Value.Unblock(site, host, isPrivate);
        if (!isPrivate) Data.AppData.NotifySyncableChanged();
        return undone;
    }

    public static void RemoveExact(string site, string domain)
    {
        Rules.Value.RemoveExact(site, domain);
        Data.AppData.NotifySyncableChanged();
    }

    public static void Forget(string site)
    {
        Rules.Value.Forget(site);
        Data.AppData.NotifySyncableChanged();
    }

    public static void ForgetPrivate() => Rules.Value.ForgetPrivate();

    public static IReadOnlyList<(string Site, string Domain)> All() => Rules.Value.All();

    // Para a sincronização: as regras, ou uma IOException se a carga falhou (o Escudo fica ilegível nessa ronda, em vez de
    // contar como «sem regras» e de as apagar nos outros computadores). Usa as regras em memória, as mesmas que a base.
    public static IReadOnlyList<(string Site, string Domain)> ReadAll()
    {
        _ = Rules.Value;
        if (_loadFailed) throw new System.IO.IOException("As regras por site não se leram.");
        return All();
    }
}
