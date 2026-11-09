using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Eteris.Browser.Shields;

// O domínio registável (eTLD+1) pela Public Suffix List — a mesma regra do Chromium para «site»: alice.github.io e
// bob.github.io são sites diferentes, como foo.blogspot.com, app.vercel.app ou saopaulo.sp.gov.br. A lista (secções
// ICANN e PRIVATE, dados MPL-2.0) vem compilada no eteris_shields.dll pelo crate psl, que o adblock-rust já usa; sem
// o DLL (ou com um antigo, sem a função) fica a aproximação do TrackerBlocker.
public static partial class PublicSuffix
{
    private const string Library = "eteris_shields";

    // Chamado por pedido de rede e por linha do histórico: os anfitriões repetem-se muito.
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private const int CacheLimit = 8192;
    private static volatile bool s_native = true;

    // Um anfitrião que é ele próprio um sufixo público (github.io), um nome sem ponto (localhost) ou um IP é o próprio site.
    public static string RegistrableDomain(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        if (Cache.TryGetValue(host, out var known)) return known;
        var site = Lookup(host) ?? host;
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[host] = site;
        return site;
    }

    // A lista nativa está a responder (não a aproximação do TrackerBlocker). O cofre só junta subdomínios com ela.
    public static bool Precise
    {
        get
        {
            if (s_native && !s_probed)
            {
                Lookup("eteris-psl-probe.github.io");
                s_probed = true;
            }
            return s_native;
        }
    }

    private static volatile bool s_probed;

    private static string? Lookup(string host)
    {
        if (s_native)
        {
            try
            {
                var value = shields_registrable_domain(host);
                if (value == 0) return null;
                try { return Marshal.PtrToStringUTF8(value); }
                finally { shields_string_free(value); }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                s_native = false;
            }
        }
        return TrackerBlocker.RegistrableDomain(host);
    }

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint shields_registrable_domain(string host);

    [LibraryImport(Library)]
    private static partial void shields_string_free(nint value);
}
