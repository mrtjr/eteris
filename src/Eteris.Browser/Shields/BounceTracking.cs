using System.Text.Json;
using CefSharp;
using Eteris.Browser.Data;

namespace Eteris.Browser.Shields;

// Esquecer os sites de salto («bounce tracking mitigation», como no Chrome e no Brave): um rastreador que se mete
// entre dois sites (site A → t.rastreador.com → site B, sem o utilizador lá parar) usa esse instante para ler e
// gravar os seus cookies como se fosse um site visitado. O Éteris segue a cadeia de cada aba, marca os sites que só
// serviram de salto e, passado o prazo, apaga-lhes os cookies e o armazenamento — a não ser que o utilizador tenha
// visitado o site a sério (clicou ou escreveu nele) nos últimos 45 dias.

// Cadeia de navegações de uma aba, do site onde o utilizador estava até onde a página parou.
public sealed class RedirectChain
{
    // Uma página que navega sozinha depois disto já não foi um salto: o utilizador esteve lá.
    public static readonly TimeSpan DwellLimit = TimeSpan.FromSeconds(10);

    private string? _initiatorSite;
    private readonly List<(string Url, DateTime At)> _hops = [];

    // Sobe a cada passo: quem espera para fechar a cadeia sabe se ela mudou entretanto.
    public int Version { get; private set; }

    public int Hops => _hops.Count;

    // A navegação continua sem o utilizador: redirecionamento do servidor, ou do script/meta refresh sem gesto.
    public static bool Continues(bool userGesture, bool isRedirect, TransitionType transition)
    {
        if (isRedirect) return true;
        if (userGesture) return false;
        if ((transition & TransitionType.ForwardBack) != 0) return false;
        return (transition & TransitionType.ClientRedirect) != 0;
    }

    // Escrito na barra, favorito ou página inicial: o utilizador quis ir a esse site.
    public static bool IsTyped(TransitionType transition)
        => (transition & TransitionType.SourceMask) is TransitionType.Explicit or TransitionType.AutoBookmark
            or TransitionType.Keyword or TransitionType.KeywordGenerated or TransitionType.Generated;

    // O utilizador navegou (clique, barra, favorito): fecha a cadeia anterior (devolve os seus saltos) e começa outra.
    public IReadOnlyList<string> Begin(string? initiatorUrl, string url, DateTime now)
    {
        var closed = Close();
        _initiatorSite = SiteScope.SiteOf(initiatorUrl);
        _hops.Add((url, now));
        Version++;
        return closed;
    }

    // A navegação continua sem o utilizador. «rewritten»: foi o próprio Éteris a trocar o endereço (salto de rastreio,
    // limpeza, HTTPS) — o anterior nunca chegou a carregar, por isso não conta como passo.
    public IReadOnlyList<string> Continue(string? currentUrl, string url, DateTime now, bool serverRedirect, bool rewritten)
    {
        if (_hops.Count == 0) return Begin(currentUrl, url, now);
        if (rewritten)
        {
            _hops[^1] = (url, now);
            return [];
        }
        if (!serverRedirect && now - _hops[^1].At > DwellLimit) return Begin(_hops[^1].Url, url, now);
        _hops.Add((url, now));
        Version++;
        return [];
    }

    // Sites que só serviram de salto: os do meio da cadeia, fora o de partida e o de chegada. Uma ida e volta ao
    // site de partida (entrar com a conta de outro site) não conta: é assim que funcionam os logins.
    public IReadOnlyList<string> Close()
    {
        var hops = _hops.Select(h => SiteScope.SiteOf(h.Url)).ToList();
        var initiator = _initiatorSite;
        _hops.Clear();
        _initiatorSite = null;
        Version++;
        if (hops.Count < 2) return [];
        var final = hops[^1];
        if (final is not null && final == initiator) return [];
        return hops.Take(hops.Count - 1)
            .Where(s => s is not null && s != initiator && s != final && !LocalNetwork.IsLocal(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

// Registo dos saltos e das visitas a sério, com o relógio injetado (testável). Fica num ficheiro do perfil normal;
// a janela privada não o usa (tudo dela é esquecido ao fechar).
public sealed class BounceLedger(Func<DateTime> clock)
{
    // Como o Chrome: uma hora de tolerância e 45 dias de memória das visitas.
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);
    public static readonly TimeSpan InteractionMemory = TimeSpan.FromDays(45);

    private readonly Dictionary<string, DateTime> _bounces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _interactions = new(StringComparer.Ordinal);

    public bool Dirty { get; private set; }

    public int Cleared { get; private set; }

    private bool RecentlyVisited(string site, DateTime now)
        => _interactions.TryGetValue(site, out var at) && now - at < InteractionMemory;

    // Devolve true quando o site fica marcado (não foi visitado a sério há pouco).
    public bool RecordBounce(string site)
    {
        var now = clock();
        if (RecentlyVisited(site, now)) return false;
        if (!_bounces.ContainsKey(site))
        {
            _bounces[site] = now;
            Dirty = true;
        }
        return true;
    }

    public void RecordInteraction(string site)
    {
        _interactions[site] = clock();
        _bounces.Remove(site);
        Dirty = true;
    }

    public int PendingCount => _bounces.Count;

    public IReadOnlyList<KeyValuePair<string, DateTime>> Pending
        => _bounces.OrderBy(b => b.Value).ThenBy(b => b.Key, StringComparer.Ordinal).ToList();

    // Quando é que o site marcado perde o armazenamento.
    public DateTime? DueAt(string site) => _bounces.TryGetValue(site, out var at) ? at + Grace : null;

    // Sites cujo prazo passou, sem visita a sério entretanto. «keep»: sites a não tocar agora (uma aba aberta nele)
    // — ficam marcados e voltam a ser vistos no próximo ciclo.
    public IReadOnlyList<string> Due(Func<string, bool>? keep = null)
    {
        var now = clock();
        foreach (var site in _bounces.Keys.Where(s => RecentlyVisited(s, now)).ToList())
        {
            _bounces.Remove(site);
            Dirty = true;
        }
        return _bounces.Where(b => now - b.Value >= Grace && keep?.Invoke(b.Key) != true)
            .OrderBy(b => b.Value).Select(b => b.Key).ToList();
    }

    public void MarkCleared(string site)
    {
        if (_bounces.Remove(site)) Cleared++;
        Dirty = true;
    }

    // «Triturar» e «Esquecer ao fechar» apagam também o que o Éteris sabe do site.
    public void Forget(string site)
    {
        Dirty |= _bounces.Remove(site) | _interactions.Remove(site);
    }

    // Apagar o histórico desde uma data apaga também as visitas registadas desde então.
    public void ForgetInteractionsSince(DateTime sinceUtc)
    {
        foreach (var site in _interactions.Where(i => i.Value >= sinceUtc).Select(i => i.Key).ToList())
        {
            _interactions.Remove(site);
            Dirty = true;
        }
    }

    private sealed record Snapshot(Dictionary<string, DateTime> Bounces, Dictionary<string, DateTime> Interactions, int Cleared);

    public string Serialize()
    {
        var now = clock();
        Dirty = false;
        return JsonSerializer.Serialize(new Snapshot(
            new Dictionary<string, DateTime>(_bounces),
            _interactions.Where(i => now - i.Value < InteractionMemory).ToDictionary(i => i.Key, i => i.Value),
            Cleared));
    }

    public static BounceLedger Load(string? json, Func<DateTime> clock)
    {
        var ledger = new BounceLedger(clock);
        if (string.IsNullOrWhiteSpace(json)) return ledger;
        try
        {
            if (JsonSerializer.Deserialize<Snapshot>(json) is not { } snapshot) return ledger;
            var now = clock();
            foreach (var (site, at) in snapshot.Bounces ?? []) ledger._bounces[site] = at;
            foreach (var (site, at) in snapshot.Interactions ?? [])
                if (now - at < InteractionMemory) ledger._interactions[site] = at;
            ledger.Cleared = snapshot.Cleared;
        }
        catch (JsonException) { }
        return ledger;
    }
}

public static class BounceText
{
    // Linha do Escudo com os sites de salto a caminho desta página (vazia sem saltos).
    public static string ShieldLine(IReadOnlyList<string> sites, bool enabled)
    {
        if (sites.Count == 0) return string.Empty;
        var first = sites[0] + (sites.Count > 1 ? $" e mais {sites.Count - 1}" : string.Empty);
        var verb = sites.Count == 1 ? "serviu" : "serviram";
        return enabled
            ? $"⚠  {first} {verb} de salto sem visita: os cookies e o armazenamento são apagados dentro de 1 hora"
            : $"·  {first} {verb} de salto sem visita (esquecer os sites de salto está desligado)";
    }

    // Aviso depois de apagar.
    public static string Toast(IReadOnlyList<string> sites)
        => sites.Count == 1
            ? $"Apagámos os cookies e o armazenamento de {sites[0]}, que só serviu de salto"
            : $"Apagámos os cookies e o armazenamento de {sites.Count} sites que só serviram de salto ({string.Join(", ", sites.Take(3))}{(sites.Count > 3 ? "…" : "")})";
}
