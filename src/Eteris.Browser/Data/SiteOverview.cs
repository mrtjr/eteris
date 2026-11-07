using Eteris.Browser.Permissions;

namespace Eteris.Browser.Data;

public sealed record SiteSummary(string Site, int? ZoomPercent, bool ShieldOff, IReadOnlyList<string> Permissions, int HiddenElements)
{
    // «Modo rigoroso», «Escurecido»…
    public IReadOnlyList<string> Flags { get; init; } = [];
}

// «Definições por site»: tudo o que o Éteris guarda sobre cada site, num só lugar.
public static class SiteOverview
{
    public static string Key(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..].ToLowerInvariant() : host.ToLowerInvariant();

    public static IReadOnlyList<SiteSummary> Build(IReadOnlyDictionary<string, double> zoom, IEnumerable<string> shieldOff,
        IReadOnlyDictionary<string, IReadOnlyList<(SitePermission Kind, bool Allowed)>> permissions, IReadOnlyDictionary<string, int> hidden,
        IEnumerable<string>? strict = null, IEnumerable<string>? dark = null, IEnumerable<string>? http = null)
    {
        var flags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Flag(string host, string flag)
        {
            if (!flags.TryGetValue(Key(host), out var list)) flags[Key(host)] = list = [];
            list.Add(flag);
        }
        foreach (var host in strict ?? []) Flag(host, "modo rigoroso");
        foreach (var host in dark ?? []) Flag(host, "escurecido");
        // «Sempre para este site» do «HTTPS primeiro»: aparece aqui para o «Repor» a poder retirar (a chave é o host
        // exato, como a exceção — velho.exemplo.pt não se esconde atrás de exemplo.pt).
        foreach (var host in http ?? []) Flag(host, "sempre por http://");
        var sites = new SortedDictionary<string, (int? Zoom, bool Off, List<string> Perms, int Hidden)>(StringComparer.Ordinal);
        (int? Zoom, bool Off, List<string> Perms, int Hidden) Get(string host)
            => sites.TryGetValue(Key(host), out var s) ? s : (null, false, [], 0);

        foreach (var (host, level) in zoom)
        {
            var s = Get(host);
            sites[Key(host)] = s with { Zoom = Palette.ZoomSteps.Percent(level) };
        }
        foreach (var host in shieldOff)
        {
            var s = Get(host);
            sites[Key(host)] = s with { Off = true };
        }
        foreach (var (site, list) in permissions)
        {
            var s = Get(site);
            s.Perms.AddRange(list.Select(p => $"{PermissionText.Name(p.Kind)}: {(p.Allowed ? "permitido" : "bloqueado")}"));
            sites[Key(site)] = s;
        }
        foreach (var (host, count) in hidden)
        {
            var s = Get(host);
            sites[Key(host)] = s with { Hidden = s.Hidden + count };
        }
        foreach (var host in flags.Keys)
            if (!sites.ContainsKey(host)) sites[host] = (null, false, [], 0);
        return sites.Select(p => new SiteSummary(p.Key, p.Value.Zoom, p.Value.Off, p.Value.Perms, p.Value.Hidden)
        {
            Flags = flags.TryGetValue(p.Key, out var list) ? list : [],
        }).ToList();
    }
}
