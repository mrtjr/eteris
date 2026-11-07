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

    // Linha do Escudo: os rastreadores escondidos desmascarados nesta página (ou que a proteção está ativa). Sem
    // maneira segura de perguntar o nome canónico (proxy no Windows sem DNS cifrado), diz que está em pausa.
    public static string ShieldLine(IReadOnlyList<string> cloaked, bool enabled, CnameSource source = CnameSource.WindowsDns)
    {
        if (!enabled) return "·  Rastreadores escondidos (CNAME): desligado nas definições";
        if (source == CnameSource.Off)
            return "·  Rastreadores escondidos (CNAME): em pausa — com um proxy no Windows, o DNS do sistema revelaria os nomes à rede (ligue o DNS cifrado)";
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

// Cache em memória (nunca em disco) com TTL, e uma só consulta em voo por nome. A consulta nunca corre no ThreadPool:
// um resolvedor bloqueante (o DNS do Windows) corre nas threads próprias de BlockingLookups, e um assíncrono (DoH pelo
// motor) não ocupa thread nenhuma. Um resolvedor que devolve null não respondeu (fila cheia, sem maneira segura de
// perguntar): nada fica em cache e o pedido segue. A cache tem tamanho limitado e as entradas vencidas saem.
public sealed class CnameCache
{
    public static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);
    // Sem CNAME, o nome volta a ser visto passado este tempo.
    public static readonly TimeSpan NoCnameTtl = TimeSpan.FromMinutes(10);
    // Nomes guardados no máximo (os subdomínios candidatos de muitos dias de navegação).
    public const int MaxEntries = 4096;

    private readonly Func<string, Task<CnameAnswer?>> _resolve;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Chain, DateTime Expires)> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> _inFlight = new(StringComparer.Ordinal);
    private int _lookups;

    private CnameCache(Func<string, Task<CnameAnswer?>> resolve, Func<DateTime> clock)
    {
        _resolve = resolve;
        _clock = clock;
    }

    // Resolvedor bloqueante: corre nas threads de «workers» (por omissão, as partilhadas do DNS do Windows).
    public CnameCache(Func<string, CnameAnswer> resolve, Func<DateTime> clock, BlockingLookups? workers = null)
        : this(host => (workers ?? BlockingLookups.Dns).TryRun(() => resolve(host)), clock) { }

    public static CnameCache FromAsync(Func<string, Task<CnameAnswer?>> resolve, Func<DateTime> clock) => new(resolve, clock);

    public int Lookups => Volatile.Read(ref _lookups);

    public int Count => _entries.Count;

    public bool TryGet(string host, out IReadOnlyList<string> chain)
    {
        if (_entries.TryGetValue(host, out var entry))
        {
            if (entry.Expires > _clock())
            {
                chain = entry.Chain;
                return true;
            }
            _entries.TryRemove(new KeyValuePair<string, (IReadOnlyList<string>, DateTime)>(host, entry));
        }
        chain = [];
        return false;
    }

    public Task<IReadOnlyList<string>> LookupAsync(string host)
    {
        if (TryGet(host, out var cached)) return Task.FromResult(cached);
        return _inFlight.GetOrAdd(host, h => new Lazy<Task<IReadOnlyList<string>>>(() => Start(h))).Value;
    }

    private Task<IReadOnlyList<string>> Start(string host)
    {
        Interlocked.Increment(ref _lookups);
        Task<CnameAnswer?> pending;
        try { pending = _resolve(host); }
        catch (Exception) { pending = Task.FromResult<CnameAnswer?>(CnameAnswer.Failed); }
        return pending.ContinueWith<IReadOnlyList<string>>(task =>
        {
            var answer = task.IsCompletedSuccessfully ? task.Result : CnameAnswer.Failed;
            if (answer is not null) Store(host, answer);
            _inFlight.TryRemove(host, out _);
            return answer?.Chain ?? [];
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Store(string host, CnameAnswer answer)
    {
        var ttl = answer.Chain.Count == 0 ? (answer.Ttl < NoCnameTtl ? answer.Ttl : NoCnameTtl)
            : answer.Ttl < MinTtl ? MinTtl : answer.Ttl > MaxTtl ? MaxTtl : answer.Ttl;
        var now = _clock();
        if (_entries.Count >= MaxEntries)
        {
            foreach (var (name, entry) in _entries)
                if (entry.Expires <= now) _entries.TryRemove(name, out _);
            // Ainda cheia (todos válidos): começa de novo — é só uma cache.
            if (_entries.Count >= MaxEntries) _entries.Clear();
        }
        _entries[host] = (answer.Chain, now + ttl);
    }

    public void Clear() => _entries.Clear();
}

// Chamadas nativas bloqueantes (DnsQuery_W) em threads próprias, nunca no ThreadPool: com um DNS que não responde
// (VPN caída, router sem resposta) cada consulta fica segundos presa, e no ThreadPool prendia as continuações de que
// os pedidos dependem (o limite de 150 ms do CNAME, os scriptlets). Poucas em paralelo e uma fila curta: cheia, a
// consulta nem se faz (null: «sem resposta», o pedido segue sem esperar).
public sealed class BlockingLookups(int threads, int maxPending)
{
    public static BlockingLookups Dns { get; } = new(threads: 3, maxPending: 32);

    private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();
    private int _pending;
    private int _started;

    public int Pending => Volatile.Read(ref _pending);

    public Task<T?> TryRun<T>(Func<T> work) where T : class
    {
        if (Interlocked.Increment(ref _pending) > maxPending)
        {
            Interlocked.Decrement(ref _pending);
            return Task.FromResult<T?>(null);
        }
        if (Interlocked.Exchange(ref _started, 1) == 0)
            for (var i = 0; i < threads; i++)
                new Thread(Work) { IsBackground = true, Name = "Éteris DNS " + (i + 1) }.Start();
        // As continuações nunca correm nestas threads (podiam ficar à espera de outra consulta).
        var done = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception e) { done.TrySetException(e); }
            finally { Interlocked.Decrement(ref _pending); }
        });
        return done.Task;
    }

    private void Work()
    {
        foreach (var job in _queue.GetConsumingEnumerable()) job();
    }
}

// Limite de consultas novas por página (por browser, reposto quando a página muda), contado por nome distinto: 30
// imagens de img.jornal.pt gastam uma consulta (as repetidas juntam-se à que está em voo), e metrics.jornal.pt ainda
// tem a sua. Uma aba fechada (ou a dormir) leva a sua conta (Forget); e nunca se guardam mais do que MaxBrowsers.
public sealed class CnameQuota(int perPage)
{
    public const int MaxBrowsers = 256;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, (string Page, HashSet<string> Names)> _pages = new();

    public int Browsers
    {
        get { lock (_lock) return _pages.Count; }
    }

    public bool TryTake(int browserId, string? pageUrl, string host)
    {
        var page = pageUrl ?? string.Empty;
        lock (_lock)
        {
            if (!_pages.TryGetValue(browserId, out var entry) || entry.Page != page)
            {
                // Uma conta esquecida a mais só dá a essa aba mais umas consultas.
                if (!_pages.ContainsKey(browserId) && _pages.Count >= MaxBrowsers) _pages.Clear();
                _pages[browserId] = entry = (page, new HashSet<string>(StringComparer.Ordinal));
            }
            if (entry.Names.Contains(host)) return true;
            if (entry.Names.Count >= perPage) return false;
            entry.Names.Add(host);
            return true;
        }
    }

    public void Forget(int browserId)
    {
        lock (_lock) _pages.Remove(browserId);
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

// Consulta ao DNS do Windows (DnsQuery_W, dnsapi.dll): os nomes CNAME da resposta, pela ordem. «cacheOnly»: só a
// cache do Windows (DNS_QUERY_NO_WIRE_QUERY), nada sai para a rede. Chamada bloqueante: só nas threads de
// BlockingLookups.Dns.
public static partial class WindowsDns
{
    private const ushort TypeA = 1;
    private const ushort TypeCname = 5;
    private const uint QueryStandard = 0;
    private const uint QueryNoWireQuery = 0x10;
    private const int FreeRecordList = 1;
    private const int NameError = 9003;

    public static CnameAnswer QueryCname(string host, bool cacheOnly)
    {
        var status = DnsQuery(host, TypeA, cacheOnly ? QueryNoWireQuery : QueryStandard, 0, out var results, 0);
        // DNS_ERROR_RCODE_NAME_ERROR com registos: o alvo do CNAME não existe, mas a cadeia vem na resposta.
        if ((status != 0 && status != NameError) || results == 0)
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

// Nome canónico por DNS-over-HTTPS (RFC 8484, formato binário), pelo próprio motor. Com o DNS cifrado ligado o
// Chromium resolve tudo por DoH e a cache do Windows nunca vê esses nomes: perguntar-lhe não desmascarava nada. A
// pergunta vai ao mesmo fornecedor (que já recebe o nome quando a página o pede), num pedido do motor sem aba
// (contexto global: DNS cifrado e proxy do motor), sem cookies e sem cache em disco; nunca ocupa uma thread à espera.
public static class DohCname
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public const int MaxInFlight = 8;
    private const ushort TypeA = 1;
    private const ushort TypeCname = 5;
    private static int s_inFlight;

    // Pergunta do tipo A (a resposta traz a cadeia CNAME antes dos endereços), com o id 0 (RFC 8484 §4.1) e recursão.
    public static byte[]? BuildQuery(string host)
    {
        host = host.Trim().TrimEnd('.');
        if (host.Length is 0 or > 253) return null;
        var bytes = new List<byte> { 0, 0, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.Any(c => c <= ' ' || c > '~')) return null;
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange([0, 0, (byte)TypeA, 0, 1]);
        return bytes.ToArray();
    }

    // O endereço GET do modelo do fornecedor (…/dns-query?dns=<base64url sem «=»>).
    public static string? QueryUrl(string template, string host)
        => BuildQuery(host) is { } query
            ? template + (template.Contains('?') ? "&" : "?") + "dns=" + Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            : null;

    // Os nomes CNAME da secção de respostas, pela ordem, e o menor TTL. Um NXDOMAIN também traz a cadeia (o nome que não
    // existe é o último, o alvo do CNAME): sem respostas, é «sem CNAME». null: resposta estragada ou com outro erro
    // (conta como falha).
    public static CnameAnswer? ParseAnswer(byte[] message)
    {
        try
        {
            if (message.Length < 12 || (message[2] & 0x80) == 0) return null;
            var rcode = message[3] & 0x0F;
            if (rcode is not (0 or 3)) return null;
            int questions = U16(message, 4), answers = U16(message, 6);
            var pos = 12;
            for (var i = 0; i < questions; i++) pos = SkipName(message, pos) + 4;
            var chain = new List<string>();
            var ttl = uint.MaxValue;
            for (var i = 0; i < answers; i++)
            {
                pos = SkipName(message, pos);
                if (pos + 10 > message.Length) return null;
                var type = U16(message, pos);
                var recordTtl = (uint)(U16(message, pos + 4) << 16 | U16(message, pos + 6));
                var length = U16(message, pos + 8);
                pos += 10;
                if (pos + length > message.Length) return null;
                if (type == TypeCname)
                {
                    chain.Add(ReadName(message, pos));
                    ttl = Math.Min(ttl, recordTtl);
                }
                pos += length;
            }
            return new CnameAnswer(chain, chain.Count == 0 || ttl == uint.MaxValue ? CnameCache.NoCnameTtl : TimeSpan.FromSeconds(ttl));
        }
        catch (FormatException) { return null; }
    }

    private static int U16(byte[] m, int at)
        => at + 1 < m.Length ? m[at] << 8 | m[at + 1] : throw new FormatException();

    private static int SkipName(byte[] m, int pos)
    {
        while (true)
        {
            if (pos >= m.Length) throw new FormatException();
            var b = m[pos];
            if (b == 0) return pos + 1;
            if ((b & 0xC0) == 0xC0) return pos + 1 < m.Length ? pos + 2 : throw new FormatException();
            if ((b & 0xC0) != 0) throw new FormatException();
            pos += 1 + b;
        }
    }

    // Um nome com ponteiros de compressão (no máximo 32 saltos, 255 octetos).
    private static string ReadName(byte[] m, int pos)
    {
        var labels = new List<string>();
        var length = 0;
        for (var jumps = 0; ;)
        {
            if (pos >= m.Length) throw new FormatException();
            var b = m[pos];
            if (b == 0) break;
            if ((b & 0xC0) == 0xC0)
            {
                if (pos + 1 >= m.Length || ++jumps > 32) throw new FormatException();
                pos = (b & 0x3F) << 8 | m[pos + 1];
                continue;
            }
            if ((b & 0xC0) != 0 || pos + 1 + b > m.Length || (length += b + 1) > 255) throw new FormatException();
            labels.Add(System.Text.Encoding.ASCII.GetString(m, pos + 1, b));
            pos += 1 + b;
        }
        return string.Join('.', labels).ToLowerInvariant();
    }

    // null: não se perguntou (motor por arrancar, demasiadas perguntas em voo, nome inválido).
    public static Task<CnameAnswer?> QueryAsync(string host, string template)
    {
        if (QueryUrl(template, host) is not { } url || Cef.IsInitialized != true) return Task.FromResult<CnameAnswer?>(null);
        if (Interlocked.Increment(ref s_inFlight) > MaxInFlight)
        {
            Interlocked.Decrement(ref s_inFlight);
            return Task.FromResult<CnameAnswer?>(null);
        }
        var done = new TaskCompletionSource<CnameAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new CancellationTokenSource(Timeout);
        var registration = timeout.Token.Register(() => done.TrySetResult(CnameAnswer.Failed));
        _ = done.Task.ContinueWith(_ =>
        {
            Interlocked.Decrement(ref s_inFlight);
            registration.Dispose();
            timeout.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var client = new Client(bytes => done.TrySetResult(bytes is null ? CnameAnswer.Failed : ParseAnswer(bytes) ?? CnameAnswer.Failed));
        _ = Cef.UIThreadTaskFactory.StartNew(() =>
        {
            try
            {
                using var request = CefSharp.Request.Create();
                request.Url = url;
                request.Method = "GET";
                request.SetReferrer(string.Empty, ReferrerPolicy.NoReferrer);
                request.SetHeaderByName("Accept", "application/dns-message", true);
                // Sem cookies (sem AllowStoredCredentials) e sem cache: os nomes perguntados não ficam no disco.
                request.Flags = UrlRequestFlags.DisableCache | UrlRequestFlags.StopOnRedirect | UrlRequestFlags.NoRetryOn5XX;
                client.Start(new CefSharp.UrlRequest(request, client));
            }
            catch (Exception) { client.Fail(); }
        });
        return done.Task;
    }

    // Junta a resposta (no máximo 64 KiB, como uma mensagem DNS) e entrega-a no fim, se for application/dns-message.
    private sealed class Client(Action<byte[]?> onDone) : IUrlRequestClient
    {
        private readonly System.IO.MemoryStream _bytes = new();
        private IUrlRequest? _request;
        private bool _tooBig;

        public void Start(IUrlRequest request) => _request = request;

        public void Fail() => onDone(null);

        bool IUrlRequestClient.GetAuthCredentials(bool isProxy, string host, int port, string realm, string scheme, IAuthCallback callback)
        {
            callback.Dispose();
            return false;
        }

        void IUrlRequestClient.OnDownloadData(IUrlRequest request, System.IO.Stream data)
        {
            var chunk = new byte[4096];
            int read;
            while ((read = data.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (_tooBig || _bytes.Length + read > 65535)
                {
                    _tooBig = true;
                    continue;
                }
                _bytes.Write(chunk, 0, read);
            }
        }

        void IUrlRequestClient.OnDownloadProgress(IUrlRequest request, long current, long total) { }

        void IUrlRequestClient.OnUploadProgress(IUrlRequest request, long current, long total) { }

        void IUrlRequestClient.OnRequestComplete(IUrlRequest request)
        {
            byte[]? body = null;
            try
            {
                var response = request.Response;
                if (!_tooBig && request.RequestStatus == UrlRequestStatus.Success && response?.StatusCode == 200
                    && string.Equals(response.MimeType, "application/dns-message", StringComparison.OrdinalIgnoreCase))
                    body = _bytes.ToArray();
            }
            catch (Exception) { }
            _bytes.Dispose();
            // Mesma thread do CEF que criou o pedido, como a API exige.
            _request?.Dispose();
            _request = null;
            onDone(body);
        }
    }
}

// Proxy configurado no Windows (o Chromium usa-o): o DNS do sistema revelaria à rede local os nomes que o proxy devia
// esconder. Lido do registo do utilizador (proxy fixo ou script PAC), no máximo a cada 30 s.
public static class SystemProxy
{
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);
    // Lida das threads de rede: uma leitura inteira de cada vez (sem tuplos rasgados).
    private sealed record Reading(bool Value, DateTime At);
    private static volatile Reading s_cached = new(false, DateTime.MinValue);

    public static bool From(object? proxyEnable, object? proxyServer, object? autoConfigUrl)
        => (proxyEnable is int on && on != 0 && proxyServer is string { Length: > 0 } server && !string.IsNullOrWhiteSpace(server))
           || autoConfigUrl is string { Length: > 0 } pac && !string.IsNullOrWhiteSpace(pac);

    public static bool Configured
    {
        get
        {
            var cached = s_cached;
            if (DateTime.UtcNow - cached.At < Refresh) return cached.Value;
            var value = false;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                value = key is not null && From(key.GetValue("ProxyEnable"), key.GetValue("ProxyServer"), key.GetValue("AutoConfigURL"));
            }
            catch (Exception) { }
            s_cached = new Reading(value, DateTime.UtcNow);
            return value;
        }
    }
}

// De onde vem o nome canónico: o DNS do Windows (rede ou só a cache), o DoH pelo motor, ou de lado nenhum.
public enum CnameSource { WindowsDns, WindowsCacheOnly, SecureDns, Off }

// As caches do Éteris. Com o DNS cifrado, o nome canónico pergunta-se por DoH pelo motor (DohCname); com um proxy no
// Windows e sem DNS cifrado, não se pergunta (o Escudo diz que está em pausa); sem nenhum dos dois, ao DNS do Windows.
// A janela privada tem a sua cache, esquecida quando a sessão privada acaba, e sem DNS cifrado só lê a cache do Windows
// (nunca faz ela própria uma consulta que lá deixasse os nomes visitados).
public static class Cname
{
    public static CnameSource SourceFor(bool secureDns, bool systemProxy, bool isPrivate)
        => secureDns ? CnameSource.SecureDns
            : systemProxy ? CnameSource.Off
            : isPrivate ? CnameSource.WindowsCacheOnly
            : CnameSource.WindowsDns;

    public static CnameSource SourceFor(bool isPrivate)
        => SourceFor(PrivacyPreferences.SecureDnsTemplate is not null, PrivacyPreferences.SecureDnsTemplate is null && SystemProxy.Configured, isPrivate);

    private static Task<CnameAnswer?> Resolve(string host, bool isPrivate) => SourceFor(isPrivate) switch
    {
        CnameSource.SecureDns => DohCname.QueryAsync(host, PrivacyPreferences.SecureDnsTemplate!),
        CnameSource.WindowsDns => BlockingLookups.Dns.TryRun(() => WindowsDns.QueryCname(host, cacheOnly: false)),
        CnameSource.WindowsCacheOnly => BlockingLookups.Dns.TryRun(() => WindowsDns.QueryCname(host, cacheOnly: true)),
        _ => Task.FromResult<CnameAnswer?>(null),
    };

    public static CnameCache Cache { get; } = CnameCache.FromAsync(host => Resolve(host, isPrivate: false), () => DateTime.UtcNow);

    public static CnameCache PrivateCache { get; } = CnameCache.FromAsync(host => Resolve(host, isPrivate: true), () => DateTime.UtcNow);

    public static CnameCache For(bool isPrivate) => isPrivate ? PrivateCache : Cache;

    public static CnameQuota Quota { get; } = new(CnameUncloak.PageQuota);
}
