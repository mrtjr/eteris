using System.Net;
using System.Net.Sockets;

namespace Eteris.Browser.Shields;

// Permissão para a rede local: uma página pública não fala com este computador (127.0.0.1, localhost) nem com a rede
// de casa (router, impressora, NAS: 192.168.x, 10.x, .local…) sem o utilizador deixar. Sem isto, qualquer site podia
// varrer a rede local, falar com serviços que só esperam o próprio PC (Discord, Steam, painéis de administração) ou
// atacar o router a partir do navegador. Abrir um endereço local na barra de endereço continua livre.
public static class LocalNetwork
{
    public enum Space { Public, Private, Loopback }

    public enum Decision { Allow, Block }

    // Onde fica um nome ou endereço: neste computador, na rede local ou na Internet.
    public static Space Classify(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return Space.Public;
        var name = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(name, out var ip)) return Classify(ip);
        if (name == "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal)) return Space.Loopback;
        // Nomes da rede de casa ou da empresa, e nomes sem ponto («router», «nas», «impressora»).
        if (!name.Contains('.') || PrivateSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal))) return Space.Private;
        return Space.Public;
    }

    private static readonly string[] PrivateSuffixes = [".local", ".internal", ".home.arpa", ".lan", ".intranet"];

    public static Space Classify(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                127 => Space.Loopback,
                // 0.0.0.0 chega a este computador em Windows, macOS e Linux (o «0.0.0.0 day»).
                0 => Space.Loopback,
                10 => Space.Private,
                172 when b[1] is >= 16 and <= 31 => Space.Private,
                192 when b[1] == 168 => Space.Private,
                169 when b[1] == 254 => Space.Private,
                // 100.64.0.0/10: NAT do operador e redes privadas como a Tailscale (o NAS, o Home Assistant).
                100 when (b[1] & 0xC0) == 64 => Space.Private,
                // 198.18.0.0/15: redes de testes e de equipamentos (como na tabela de espaços de endereços do Chromium).
                198 when b[1] is 18 or 19 => Space.Private,
                _ => Space.Public,
            };
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any)) return Space.Loopback;
            var b = ip.GetAddressBytes();
            // fc00::/7 (endereços locais únicos) e fe80::/10 (ligação local).
            if ((b[0] & 0xFE) == 0xFC || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return Space.Private;
            // 64:ff9b::/96 (NAT64): um endereço IPv4 dentro de um IPv6 — conta o IPv4 (64:ff9b::c0a8:101 é 192.168.1.1).
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
                return Classify(new IPAddress(b.AsSpan(12, 4)));
        }
        return Space.Public;
    }

    // Os endereços a que um nome resolve: local só se todos o forem (o que tiver um endereço público é público).
    public static Space Classify(IEnumerable<IPAddress> addresses)
    {
        var space = (Space?)null;
        foreach (var address in addresses)
        {
            var one = Classify(address);
            if (one == Space.Public) return Space.Public;
            space = space is null || one == Space.Private ? one : space;
        }
        return space ?? Space.Public;
    }

    public static bool IsLocal(string? host) => Classify(host) != Space.Public;

    // Um pedido de uma página (sub-recurso, fetch, XHR, WebSocket, iframe) para um endereço local: só passa se a
    // página também for local, se o utilizador o deixou para este site, ou se for a navegação de topo. A página de um
    // blob: é a de quem o criou (TopDocument); as páginas do próprio Éteris e um ficheiro local abrem o que é local; um
    // documento sem origem web (about:blank, data:, um blob: opaco) conta como público — navegar para um deles não
    // pode servir para chegar ao router.
    public static Decision Decide(string? topUrl, string requestUrl, CefSharp.ResourceType type, bool allowedForSite)
    {
        if (type == CefSharp.ResourceType.MainFrame || allowedForSite) return Decision.Allow;
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) || request.Scheme is not ("http" or "https" or "ws" or "wss"))
            return Decision.Allow;
        if (!IsLocal(request.Host)) return Decision.Allow;
        if (!Uri.TryCreate(TopDocument.Effective(topUrl), UriKind.Absolute, out var top)) return Decision.Block;
        if (top.Scheme is "eteris" or "devtools" or "chrome" or "file") return Decision.Allow;
        if (top.Scheme is not ("http" or "https")) return Decision.Block;
        return IsLocal(top.Host) ? Decision.Allow : Decision.Block;
    }

    // O site que pede (a chave da permissão): o anfitrião da página de topo, com a porta se não for a habitual.
    public static string? SiteOf(string? topUrl)
        => TopDocument.Effective(topUrl) is { } effective && Uri.TryCreate(effective, UriKind.Absolute, out var top) && top.Scheme is "http" or "https"
            ? Permissions.PermissionRules.Site(effective)
            : null;

    // O nome da página de topo quando a decisão depende do endereço a que ele resolve: um nome público (sem ser um IP
    // nem um nome local) — o portal intranet.empresa.pt em 10.1.2.3, o ha.minhacasa.pt em 192.168.1.10.
    public static string? ResolvableTop(string? topUrl)
        => Uri.TryCreate(TopDocument.Effective(topUrl), UriKind.Absolute, out var top) && top.Scheme is "http" or "https"
           && top.HostNameType == UriHostNameType.Dns && Classify(top.Host) == Space.Public
            ? top.IdnHost.ToLowerInvariant()
            : null;

    // Até onde um pedido local espera pelo endereço da página de topo (não bloqueia a thread de rede: IRequestCallback).
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(300);

    // Os endereços das páginas de topo de nome público que tentaram chegar à rede local (pelo DNS do sistema, como o
    // Chromium os resolveu). Quando todos são locais, a página é ela própria da rede local e quem decide é a verificação
    // do Chromium (LocalNetworkAccessChecks), que vê o IP a que a página se ligou de facto — um DNS que respondesse de
    // outra maneira a esta consulta não abre caminho nenhum.
    public static AddressCache Addresses { get; } = new(host => Dns.GetHostAddressesAsync(host), () => DateTime.UtcNow);

    // Como o Escudo mostra o destino travado.
    public static string Label(string requestUrl)
        => Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
            ? (Classify(uri.Host) == Space.Loopback ? "este computador" : "rede local") + " · " + (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            : requestUrl;
}

// Cache em memória, com prazo, do espaço de endereços de um nome (uma só consulta em voo por nome).
public sealed class AddressCache(Func<string, Task<IPAddress[]>> resolve, Func<DateTime> clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int Limit = 512;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (LocalNetwork.Space Space, DateTime Expires)> _entries = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<LocalNetwork.Space>>> _inFlight = new(StringComparer.Ordinal);

    public bool TryGet(string host, out LocalNetwork.Space space)
    {
        if (_entries.TryGetValue(host, out var entry) && entry.Expires > clock())
        {
            space = entry.Space;
            return true;
        }
        space = LocalNetwork.Space.Public;
        return false;
    }

    public Task<LocalNetwork.Space> LookupAsync(string host)
    {
        if (TryGet(host, out var known)) return Task.FromResult(known);
        return _inFlight.GetOrAdd(host, h => new Lazy<Task<LocalNetwork.Space>>(() => Task.Run(async () =>
        {
            LocalNetwork.Space space;
            try { space = LocalNetwork.Classify(await resolve(h).ConfigureAwait(false)); }
            catch (Exception) { space = LocalNetwork.Space.Public; }
            if (_entries.Count >= Limit) _entries.Clear();
            _entries[h] = (space, clock() + Ttl);
            _inFlight.TryRemove(h, out _);
            return space;
        }))).Value;
    }
}

// Autorizações «só desta vez» para a rede local: só em memória, até fechar o Éteris (as da janela privada à parte,
// esquecidas quando a última janela privada fecha). As de «sempre» ficam nas permissões do site.
public static class LocalNetworkGrants
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Site, bool Private), byte> Granted = new();

    public static void Allow(string site, bool isPrivate) => Granted[(site, isPrivate)] = 0;

    public static bool IsAllowed(string site, bool isPrivate) => Granted.ContainsKey((site, isPrivate));

    public static void Forget(string site)
    {
        Granted.TryRemove((site, false), out _);
        Granted.TryRemove((site, true), out _);
    }

    // As do site (todas as chaves host[:porta] que lhe pertencem), só do contexto indicado (normal ou privado).
    public static void ForgetWhere(Func<string, bool> belongs, bool isPrivate)
    {
        foreach (var key in Granted.Keys.Where(k => k.Private == isPrivate && belongs(k.Site)).ToList()) Granted.TryRemove(key, out _);
    }

    public static void ForgetPrivate()
    {
        foreach (var key in Granted.Keys.Where(k => k.Private).ToList()) Granted.TryRemove(key, out _);
    }
}

public static class LocalNetworkText
{
    // Detalhe do cartão: para onde a página tentou ir e quando faz sentido deixar.
    public static string CardDetail(string label)
        => $"Tentou chegar a {label}. Só permita se confiar neste site — por exemplo, o Discord, uma impressora ou o painel do router.";

    // Linha do Escudo: o que foi travado nesta página (ou que a proteção está ativa).
    public static string ShieldLine(IReadOnlyList<KeyValuePair<string, int>> blocked)
    {
        if (blocked.Count == 0) return "✓  A rede local e este computador estão fora do alcance dos sites";
        var total = blocked.Sum(b => b.Value);
        var first = blocked[0].Key;
        return $"✓  {total} {(total == 1 ? "pedido travado" : "pedidos travados")} para {first}"
               + (blocked.Count > 1 ? $" e mais {blocked.Count - 1}" : string.Empty);
    }
}
