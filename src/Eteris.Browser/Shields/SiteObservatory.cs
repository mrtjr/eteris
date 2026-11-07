using System.IO;
using System.Text.Json;
using CefSharp;
using Eteris.Browser.Data;

namespace Eteris.Browser.Shields;

// Tipo de pedido, como o Observatório o mostra (script, imagem, XHR…).
public enum ObservedKind : byte { Script, Image, Xhr, Frame, Style, Font, Media, Beacon, Other }

// Quem decidiu o pedido. Allowed: nenhuma regra o apanhou; ShieldOff: passou porque o utilizador desligou o Escudo no
// site; UserBlocked: o utilizador bloqueou esse domínio só neste site (Observatório).
public enum ObservedVerdict : byte { Allowed, Blocked, Strict, Cloaked, LocalNetwork, UserBlocked, ShieldOff }

// Cor do nó: vermelho (bloqueado), verde (permitido) ou âmbar (exceção do utilizador, num sentido ou no outro).
public enum NodeColor : byte { Allowed, Blocked, User }

// Um domínio contactado pela página: pedidos por tipo e quantos foram bloqueados.
public sealed record DomainStat(string Host, int Requests, int Blocked, IReadOnlyList<(ObservedKind Kind, int Count)> Kinds,
    ObservedVerdict Verdict, string? Detail, int Order)
{
    public int Allowed => Requests - Blocked;

    public NodeColor Color => Verdict is ObservedVerdict.UserBlocked or ObservedVerdict.ShieldOff ? NodeColor.User
        : Blocked > 0 ? NodeColor.Blocked : NodeColor.Allowed;
}

public sealed record ObservatorySnapshot(string? PageUrl, string? Site, int FirstPartyRequests, IReadOnlyList<DomainStat> ThirdParties,
    int Overflow, int Version)
{
    public static readonly ObservatorySnapshot Empty = new(null, null, 0, [], 0, 0);
}

// Uma empresa e os seus domínios contactados pela página (ou «Outros» para os sem dono conhecido).
public sealed record CompanyGroup(string Name, bool Known, IReadOnlyList<DomainStat> Domains)
{
    public int Requests => Domains.Sum(d => d.Requests);
    public int Blocked => Domains.Sum(d => d.Blocked);
}

// O Observatório do site: cada domínio que a página contactou, com quantos pedidos, de que tipo e o que o Escudo
// decidiu. Um por aba, alimentado pela thread de rede do Chromium (PrivacyRequestHandler): o registo de um pedido é
// um incremento num dicionário — o anfitrião é lido do endereço sem criar texto nem Uri, e um domínio já visto não
// reserva memória. Agrupar por empresa, dispor os nós e explicar fica para quando o painel abre (Snapshot).
public sealed class SiteObservatory
{
    // Para lá disto (páginas com centenas de domínios), os novos contam só no total.
    public const int MaxHosts = 400;

    private static readonly int KindCount = Enum.GetValues<ObservedKind>().Length;

    private sealed class Entry(int order)
    {
        public readonly int Order = order;
        public readonly int[] Kinds = new int[KindCount];
        public int Requests;
        public int Blocked;
        public ObservedVerdict Verdict;
        public string? Detail;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private string? _pageUrl;
    private string? _pageHost;
    private int _overflow;
    private int _version;

    public SiteObservatory() => _lookup = _hosts.GetAlternateLookup<ReadOnlySpan<char>>();

    // Sobe a cada pedido registado: o painel só se redesenha quando mudou.
    public int Version => Volatile.Read(ref _version);

    // Uma navegação nova da página principal: começa do zero.
    public void Reset(string pageUrl)
    {
        lock (_lock)
        {
            _hosts.Clear();
            _overflow = 0;
            _pageUrl = pageUrl;
            _pageHost = HostOf(pageUrl).ToString();
            _version++;
        }
    }

    public static ObservedKind KindOf(ResourceType type) => type switch
    {
        ResourceType.Script or ResourceType.Worker or ResourceType.SharedWorker or ResourceType.ServiceWorker => ObservedKind.Script,
        ResourceType.Image or ResourceType.Favicon => ObservedKind.Image,
        ResourceType.Xhr => ObservedKind.Xhr,
        ResourceType.SubFrame => ObservedKind.Frame,
        ResourceType.Stylesheet => ObservedKind.Style,
        ResourceType.FontResource => ObservedKind.Font,
        ResourceType.Media => ObservedKind.Media,
        ResourceType.Ping or ResourceType.CspReport => ObservedKind.Beacon,
        _ => ObservedKind.Other,
    };

    public void Record(string requestUrl, ResourceType type, ObservedVerdict verdict, string? detail = null)
        => Record(requestUrl, KindOf(type), verdict, detail);

    public void Record(string requestUrl, ObservedKind kind, ObservedVerdict verdict, string? detail = null)
    {
        var host = HostOf(requestUrl);
        if (host.IsEmpty) return;
        lock (_lock)
        {
            if (!_lookup.TryGetValue(host, out var entry))
            {
                if (_hosts.Count >= MaxHosts)
                {
                    _overflow++;
                    _version++;
                    return;
                }
                entry = new Entry(_hosts.Count);
                _lookup[host] = entry;
            }
            entry.Kinds[(int)kind]++;
            entry.Requests++;
            if (verdict is not (ObservedVerdict.Allowed or ObservedVerdict.ShieldOff)) entry.Blocked++;
            // A decisão mais forte fica: um bloqueio não é apagado por um pedido permitido do mesmo domínio.
            if (entry.Requests == 1 || verdict > entry.Verdict)
            {
                entry.Verdict = verdict;
                if (detail is not null) entry.Detail = detail;
            }
            _version++;
        }
    }

    // O anfitrião de um endereço http(s)/ws(s), sem utilizador nem porta; vazio para outros esquemas.
    public static ReadOnlySpan<char> HostOf(ReadOnlySpan<char> url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme is < 2 or > 5) return default;
        var name = url[..scheme];
        if (!name.Equals("http", StringComparison.OrdinalIgnoreCase) && !name.Equals("https", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("ws", StringComparison.OrdinalIgnoreCase) && !name.Equals("wss", StringComparison.OrdinalIgnoreCase))
            return default;
        var rest = url[(scheme + 3)..];
        var end = rest.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            return close > 0 ? authority[..(close + 1)] : default;
        }
        var colon = authority.IndexOf(':');
        return colon >= 0 ? authority[..colon] : authority;
    }

    // Cópia para o painel: os terceiros (outro site que não o da página) pela ordem em que apareceram.
    public ObservatorySnapshot Snapshot()
    {
        lock (_lock)
        {
            var site = string.IsNullOrEmpty(_pageHost) ? null : SiteScope.OfHost(_pageHost);
            var firstParty = 0;
            var third = new List<DomainStat>();
            foreach (var (host, e) in _hosts)
            {
                if (site is not null && SiteScope.OfHost(host) == site)
                {
                    firstParty += e.Requests;
                    continue;
                }
                var kinds = new List<(ObservedKind, int)>();
                for (var k = 0; k < e.Kinds.Length; k++)
                    if (e.Kinds[k] > 0) kinds.Add(((ObservedKind)k, e.Kinds[k]));
                kinds.Sort((a, b) => b.Item2.CompareTo(a.Item2));
                third.Add(new DomainStat(host, e.Requests, e.Blocked, kinds, e.Verdict, e.Detail, e.Order));
            }
            third.Sort((a, b) => a.Order.CompareTo(b.Order));
            return new ObservatorySnapshot(_pageUrl, site, firstParty, third, _overflow, _version);
        }
    }

    // Agrupado por empresa (mapa embutido): as empresas com mais pedidos primeiro, «Outros» no fim.
    public static IReadOnlyList<CompanyGroup> ByCompany(IReadOnlyList<DomainStat> domains, CompanyMap? owners = null)
    {
        owners ??= CompanyMap.Default;
        var known = new Dictionary<string, List<DomainStat>>(StringComparer.Ordinal);
        var others = new List<DomainStat>();
        foreach (var d in domains)
        {
            if (owners.OwnerOf(d.Host) is { } owner)
            {
                if (!known.TryGetValue(owner, out var list)) known[owner] = list = [];
                list.Add(d);
            }
            else others.Add(d);
        }
        var groups = known.Select(p => new CompanyGroup(p.Key, true, p.Value.OrderByDescending(d => d.Requests).ThenBy(d => d.Order).ToList()))
            .OrderByDescending(g => g.Requests).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        if (others.Count > 0) groups.Add(new CompanyGroup("Outros", false, others.OrderByDescending(d => d.Requests).ThenBy(d => d.Order).ToList()));
        return groups;
    }

    // Linha do Escudo: com quem a página falou, por empresa («Google (3), Meta (1) e mais 2»).
    public static string Summary(IReadOnlyList<CompanyGroup> groups)
    {
        var domains = groups.Sum(g => g.Domains.Count);
        if (domains == 0) return "Esta página não contactou outros sites";
        var named = groups.Where(g => g.Known).ToList();
        var head = domains == 1 ? "Esta página contactou 1 domínio de terceiros" : $"Esta página contactou {domains} domínios de terceiros";
        if (named.Count == 0) return head;
        var shown = named.Take(2).Select(g => $"{g.Name} ({g.Domains.Count})").ToList();
        var rest = named.Count - shown.Count;
        return head + ": " + string.Join(", ", shown) + (rest switch { 0 => string.Empty, 1 => " e mais 1 empresa", _ => $" e mais {rest} empresas" });
    }

    public static string KindLabel(ObservedKind kind) => kind switch
    {
        ObservedKind.Script => "script",
        ObservedKind.Image => "imagem",
        ObservedKind.Xhr => "XHR/fetch",
        ObservedKind.Frame => "frame",
        ObservedKind.Style => "estilo",
        ObservedKind.Font => "fonte",
        ObservedKind.Media => "multimédia",
        ObservedKind.Beacon => "beacon",
        _ => "outro",
    };

    // «3 scripts · 2 imagens»: os tipos de um domínio, do mais frequente ao menos.
    public static string KindsText(DomainStat d)
        => string.Join(" · ", d.Kinds.Select(k => $"{k.Count} {Plural(KindLabel(k.Kind), k.Count)}"));

    private static string Plural(string label, int n) => n == 1 || label.Contains('/') ? label
        : label.EndsWith("m", StringComparison.Ordinal) ? label[..^1] + "ns"
        : label == "multimédia" ? label
        : label + "s";

    // «1 de 1 pedido bloqueado», «3 de 5 pedidos bloqueados».
    public static string BlockedOf(int blocked, int requests)
        => requests == 1 ? $"{blocked} de 1 pedido bloqueado" : $"{blocked} de {requests} pedidos bloqueados";

    // Quem decidiu, em palavras.
    public static string VerdictText(DomainStat d) => d.Verdict switch
    {
        ObservedVerdict.Blocked => d.Blocked == d.Requests ? "Bloqueado pelas listas do Escudo" : BlockedOf(d.Blocked, d.Requests) + " pelas listas do Escudo",
        ObservedVerdict.Strict => "Bloqueado pelo modo rigoroso deste site",
        ObservedVerdict.Cloaked => d.Detail is { } target ? $"Rastreador escondido (CNAME → {target}), bloqueado" : "Rastreador escondido (CNAME), bloqueado",
        ObservedVerdict.LocalNetwork => "Travado: a página quis falar com a sua rede local",
        ObservedVerdict.UserBlocked => "Bloqueado por si, só neste site",
        ObservedVerdict.ShieldOff => "Permitido: desligou o Escudo neste site",
        _ => "Permitido: nenhuma regra o apanhou",
    };
}

// Mapa domínio → empresa (Shields/resources/owners.json, escrito pelo projeto). Um domínio cobre os subdomínios.
public sealed class CompanyMap
{
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<CompanyMap> LazyDefault = new(() =>
    {
        using var stream = typeof(CompanyMap).Assembly.GetManifestResourceStream("Eteris.Browser.Shields.owners.json")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static CompanyMap Default => LazyDefault.Value;

    public IReadOnlyCollection<string> Companies => _owners.Values.Distinct().ToList();

    public int DomainCount => _owners.Count;

    public static CompanyMap Parse(string json)
    {
        var map = new CompanyMap();
        using var doc = JsonDocument.Parse(json);
        foreach (var company in doc.RootElement.GetProperty("empresas").EnumerateArray())
        {
            var name = company.GetProperty("nome").GetString()!;
            foreach (var domain in company.GetProperty("dominios").EnumerateArray())
                map._owners[domain.GetString()!.Trim().ToLowerInvariant()] = name;
        }
        return map;
    }

    // O dono do anfitrião ou de um domínio acima dele (ads.doubleclick.net → Google), ou null.
    public string? OwnerOf(string host)
    {
        var candidate = host.Trim().TrimEnd('.');
        while (candidate.Length > 0)
        {
            if (_owners.TryGetValue(candidate, out var owner)) return owner;
            var dot = candidate.IndexOf('.');
            if (dot < 0) return null;
            candidate = candidate[(dot + 1)..];
        }
        return null;
    }
}
