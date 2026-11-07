using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CefSharp;
using Eteris.Browser.Data;

namespace Eteris.Browser.Shields;

// Desmascarar CNAME (como o uBlock Origin no Firefox e o Brave): um site serve um rastreador por um subdomínio seu
// (metrics.jornal.pt), que no DNS é só um outro nome para o rastreador (CNAME → jornal.eulerian.net). As listas olham
// para o nome e deixam passar. O Éteris pergunta ao DNS o nome canónico e volta a perguntar às listas como se o pedido
// fosse para ele. Só para sub-recursos de subdomínios do próprio site que as listas deixaram passar, com o Escudo
// ligado no site; o pedido nunca fica à espera mais do que um instante (ver PrivacyRequestHandler).
public static class CnameUncloak
{
    // Até onde um pedido espera pela resposta do DNS antes de seguir (sem bloquear a thread de rede do Chromium).
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(150);

    // Consultas novas ao DNS por página (as respostas em cache não contam).
    public const int PageQuota = 16;

    private static readonly HashSet<ResourceType> Types =
    [
        ResourceType.Script, ResourceType.Xhr, ResourceType.Image, ResourceType.Ping, ResourceType.SubResource,
        ResourceType.Stylesheet, ResourceType.Media, ResourceType.SubFrame, ResourceType.Object,
    ];

    // Um sub-recurso servido por outro nome do próprio site (não o da página): é aí que os rastreadores se escondem.
    public static bool IsCandidate(string? pageUrl, string requestUrl, ResourceType type)
    {
        if (!Types.Contains(type)) return false;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)) return false;
        if (request.Scheme is not ("http" or "https") || page.Scheme is not ("http" or "https")) return false;
        if (request.HostNameType != UriHostNameType.Dns || LocalNetwork.IsLocal(request.Host)) return false;
        var host = request.Host.ToLowerInvariant();
        if (host == page.Host.ToLowerInvariant()) return false;
        return SiteScope.OfHost(host) == SiteScope.OfHost(page.Host);
    }

    // O primeiro nome da cadeia CNAME que as listas bloqueiam (como se o pedido fosse para ele), ou null.
    // Nomes do próprio site na cadeia (aliases internos) não se consultam.
    public static string? CloakedTracker(string requestUrl, string? documentUrl, string resourceType, IReadOnlyList<string> chain,
        Func<string, string?, string, bool> shouldBlock)
    {
        if (chain.Count == 0 || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)) return null;
        var site = SiteScope.OfHost(request.Host);
        foreach (var raw in chain)
        {
            var target = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (target.Length == 0 || SiteScope.OfHost(target) == site) continue;
            if (Uri.CheckHostName(target) != UriHostNameType.Dns) continue;
            if (shouldBlock(RewriteHost(request, target), documentUrl, resourceType)) return target;
        }
        return null;
    }

    public static string RewriteHost(Uri url, string host)
        => new UriBuilder(url) { Host = host }.Uri.AbsoluteUri;

    // Etiqueta do Escudo: o nome visível e o rastreador que estava por trás.
    public static string Label(string host, string target) => $"{host} → {target}";

    // Linha do Escudo: os rastreadores escondidos desmascarados nesta página (ou que a proteção está ativa).
    public static string ShieldLine(IReadOnlyList<string> cloaked, bool enabled)
    {
        if (!enabled) return "·  Rastreadores escondidos (CNAME): desligado nas definições";
        if (cloaked.Count == 0) return "✓  Rastreadores escondidos atrás de subdomínios do site são desmascarados";
        var labels = cloaked.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var count = cloaked.Count == 1 ? "1 rastreador escondido desmascarado" : $"{cloaked.Count} rastreadores escondidos desmascarados";
        return $"✓  {count}: {labels[0]}" + (labels.Count > 1 ? $" e mais {labels.Count - 1}" : string.Empty);
    }
}

// Resposta do DNS: os nomes canónicos por ordem (vazia sem CNAME) e por quanto tempo vale.
public sealed record CnameAnswer(IReadOnlyList<string> Chain, TimeSpan Ttl)
{
    public static readonly CnameAnswer Failed = new([], TimeSpan.FromMinutes(1));
}

// Cache em memória (nunca em disco) com TTL, e uma só consulta em voo por nome.
public sealed class CnameCache(Func<string, CnameAnswer> resolve, Func<DateTime> clock)
{
    public static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);
    // Sem CNAME, o nome volta a ser visto passado este tempo.
    public static readonly TimeSpan NoCnameTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Chain, DateTime Expires)> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> _inFlight = new(StringComparer.Ordinal);
    private int _lookups;

    public int Lookups => Volatile.Read(ref _lookups);

    public int Count => _entries.Count;

    public bool TryGet(string host, out IReadOnlyList<string> chain)
    {
        if (_entries.TryGetValue(host, out var entry) && entry.Expires > clock())
        {
            chain = entry.Chain;
            return true;
        }
        chain = [];
        return false;
    }

    public Task<IReadOnlyList<string>> LookupAsync(string host)
    {
        if (TryGet(host, out var cached)) return Task.FromResult(cached);
        var lazy = _inFlight.GetOrAdd(host, h => new Lazy<Task<IReadOnlyList<string>>>(() => Task.Run(() =>
        {
            Interlocked.Increment(ref _lookups);
            CnameAnswer answer;
            try { answer = resolve(h); }
            catch (Exception) { answer = CnameAnswer.Failed; }
            var ttl = answer.Chain.Count == 0 ? (answer.Ttl < NoCnameTtl ? answer.Ttl : NoCnameTtl)
                : answer.Ttl < MinTtl ? MinTtl : answer.Ttl > MaxTtl ? MaxTtl : answer.Ttl;
            _entries[h] = (answer.Chain, clock() + ttl);
            _inFlight.TryRemove(h, out _);
            return answer.Chain;
        })));
        return lazy.Value;
    }

    public void Clear() => _entries.Clear();
}

// Limite de consultas novas por página (por browser, reposto quando a página muda), contado por nome distinto: 30
// imagens de img.jornal.pt gastam uma consulta (as repetidas juntam-se à que está em voo), e metrics.jornal.pt ainda
// tem a sua.
public sealed class CnameQuota(int perPage)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, (string Page, HashSet<string> Names)> _pages = new();

    public bool TryTake(int browserId, string? pageUrl, string host)
    {
        var page = pageUrl ?? string.Empty;
        lock (_lock)
        {
            if (!_pages.TryGetValue(browserId, out var entry) || entry.Page != page)
                _pages[browserId] = entry = (page, new HashSet<string>(StringComparer.Ordinal));
            if (entry.Names.Contains(host)) return true;
            if (entry.Names.Count >= perPage) return false;
            entry.Names.Add(host);
            return true;
        }
    }
}

// Custo medido por pedido: quanto tempo o Éteris gasta a decidir na thread de rede e quanto um pedido esperou pelo DNS.
public static class CnameStats
{
    private static long _decisions, _decisionTicks, _waits, _waitTicks, _blocked, _timeouts;

    public static void Decision(long ticks)
    {
        Interlocked.Increment(ref _decisions);
        Interlocked.Add(ref _decisionTicks, ticks);
    }

    public static void Waited(long ticks, bool timedOut)
    {
        Interlocked.Increment(ref _waits);
        Interlocked.Add(ref _waitTicks, ticks);
        if (timedOut) Interlocked.Increment(ref _timeouts);
    }

    public static void Blocked() => Interlocked.Increment(ref _blocked);

    public static long BlockedCount => Interlocked.Read(ref _blocked);

    public static long DecisionCount => Interlocked.Read(ref _decisions);

    // Tempo médio na thread de rede por pedido candidato, em microssegundos.
    public static double MicrosPerDecision
    {
        get
        {
            var decisions = Interlocked.Read(ref _decisions);
            return decisions == 0 ? 0 : Interlocked.Read(ref _decisionTicks) * 1_000_000.0 / Stopwatch.Frequency / decisions;
        }
    }

    public static string Summary()
    {
        var decisions = Interlocked.Read(ref _decisions);
        var waits = Interlocked.Read(ref _waits);
        var perDecision = decisions == 0 ? 0 : Interlocked.Read(ref _decisionTicks) * 1_000_000.0 / Stopwatch.Frequency / decisions;
        var perWait = waits == 0 ? 0 : Interlocked.Read(ref _waitTicks) * 1000.0 / Stopwatch.Frequency / waits;
        return FormattableString.Invariant(
            $"{decisions} decisões ({perDecision:0.0} µs cada na thread de rede), {waits} esperas pelo DNS ({perWait:0.0} ms em média, {Interlocked.Read(ref _timeouts)} seguiram sem resposta), {Interlocked.Read(ref _blocked)} bloqueados");
    }
}

// Consulta ao DNS do Windows (DnsQuery_W, dnsapi.dll): os nomes CNAME da resposta, pela ordem. Com o DNS cifrado do
// Éteris ligado, só a cache do Windows é consultada (DNS_QUERY_NO_WIRE_QUERY): nada sai em claro para a rede.
public static partial class WindowsDns
{
    private const ushort TypeA = 1;
    private const ushort TypeCname = 5;
    private const uint QueryStandard = 0;
    private const uint QueryNoWireQuery = 0x10;
    private const int FreeRecordList = 1;

    public static CnameAnswer QueryCname(string host, bool cacheOnly)
    {
        var status = DnsQuery(host, TypeA, cacheOnly ? QueryNoWireQuery : QueryStandard, 0, out var results, 0);
        if (status != 0 || results == 0)
        {
            if (results != 0) DnsRecordListFree(results, FreeRecordList);
            return CnameAnswer.Failed;
        }
        try
        {
            var chain = new List<string>();
            var ttl = uint.MaxValue;
            // DNS_RECORDW: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, Data (CNAME: pNameHost).
            var ptr = IntPtr.Size;
            for (var record = results; record != 0; record = Marshal.ReadIntPtr(record))
            {
                var type = (ushort)Marshal.ReadInt16(record, 2 * ptr);
                var section = (uint)Marshal.ReadInt32(record, 2 * ptr + 4) & 0x3;
                if (type != TypeCname || section != 1) continue;
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, 2 * ptr + 16)) is { Length: > 0 } target) chain.Add(target);
                ttl = Math.Min(ttl, (uint)Marshal.ReadInt32(record, 2 * ptr + 8));
            }
            return new CnameAnswer(chain, chain.Count == 0 || ttl == uint.MaxValue ? CnameCache.NoCnameTtl : TimeSpan.FromSeconds(ttl));
        }
        finally { DnsRecordListFree(results, FreeRecordList); }
    }

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DnsQuery(string name, ushort type, uint options, nint extra, out nint results, nint reserved);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsRecordListFree(nint records, int freeType);
}

// A cache partilhada pelo Éteris: pergunta ao DNS do Windows (só à cache com o DNS cifrado ligado). A janela privada
// tem a sua, que só lê a cache do Windows (nunca faz ela própria uma consulta que lá deixasse os nomes visitados) e
// é esquecida quando a sessão privada acaba.
public static class Cname
{
    public static CnameCache Cache { get; } = new(
        host => WindowsDns.QueryCname(host, cacheOnly: PrivacyPreferences.SecureDnsProvider is not null), () => DateTime.UtcNow);

    public static CnameCache PrivateCache { get; } = new(host => WindowsDns.QueryCname(host, cacheOnly: true), () => DateTime.UtcNow);

    public static CnameCache For(bool isPrivate) => isPrivate ? PrivateCache : Cache;

    public static CnameQuota Quota { get; } = new(CnameUncloak.PageQuota);
}
