using System.Text.Json;

namespace Eteris.Browser.Shields;

// Que script de site está registado em cada separador (hostname → identificador do DevTools), sem DevTools: a lógica
// pura do ScriptletInjector. Os registos acumulam-se; só os mais antigos saem, e só quando a aba está parada.
// Cada registo guarda a «chave» do que o gerou (escudo ligado, listas carregadas, modo escuro…): o mesmo site com a
// mesma chave não volta a ser calculado nem registado (o CSS das listas nem sai sempre pela mesma ordem).
public sealed class HostScriptRegistry(int capacity)
{
    public sealed record Change(string Host, string? OldId, string? Script);

    private sealed class Entry
    {
        public string Key = string.Empty;
        public string? Script;
        public string? Id;
        public long Stamp;
        public Task Pending = Task.CompletedTask;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _clock;

    public int Count => _entries.Count;

    public bool Contains(string host) => _entries.ContainsKey(host);

    // O site já está registado com esta chave (passa a ser o mais recente).
    public bool IsCurrent(string host, string key)
    {
        if (!_entries.TryGetValue(host, out var entry) || entry.Key != key) return false;
        entry.Stamp = ++_clock;
        return true;
    }

    // Só páginas web têm scripts por site (as internas vivem do script base).
    public static Uri? HostOf(string? url)
        => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Length > 0
            ? uri
            : null;

    // O que fazer para o site ficar com este script (gerado com esta chave): null quando não há nada a mudar. Um site
    // sem script também fica lembrado, para não ser recalculado a cada link.
    public Change? Plan(string host, string key, string? script)
    {
        _entries.TryGetValue(host, out var entry);
        entry ??= _entries[host] = new Entry();
        entry.Stamp = ++_clock;
        entry.Key = key;
        if (entry.Script == script && (script is null || entry.Id is not null || !entry.Pending.IsCompleted)) return null;
        var change = new Change(host, entry.Id, script);
        entry.Script = script;
        entry.Id = null;
        return change;
    }

    public Task Pending(string host) => _entries.TryGetValue(host, out var entry) ? entry.Pending : Task.CompletedTask;

    public void SetPending(string host, Task pending)
    {
        if (_entries.TryGetValue(host, out var entry)) entry.Pending = pending;
    }

    // O DevTools confirmou o registo. Devolve um identificador a tirar quando, entretanto, o site mudou de script ou
    // saiu (este registo ficou a mais).
    public string? Registered(string host, string script, string id)
    {
        if (_entries.TryGetValue(host, out var entry) && entry.Script == script && entry.Id is null)
        {
            entry.Id = id;
            return null;
        }
        return id;
    }

    // Os registos a tirar para ficarem no máximo «capacity» sites: os mais antigos, nunca o do site à vista. Um registo
    // ainda a caminho sai quando o DevTools o confirmar (Registered devolve-o como a mais).
    public IReadOnlyList<string> Evict(string? keep)
    {
        var excess = _entries.Count - capacity;
        if (excess <= 0) return [];
        var gone = _entries.Where(p => !string.Equals(p.Key, keep, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Value.Stamp).Take(excess).ToList();
        foreach (var (host, _) in gone) _entries.Remove(host);
        return gone.Where(p => p.Value.Id is not null).Select(p => p.Value.Id!).ToList();
    }

    // A página de topo do documento (num iframe, pela última origem de location.ancestorOrigins): os scripts de um site
    // correm nas páginas desse site e nos iframes delas, como quando havia um só registo por separador.
    private const string TopLocation = """
          let protocol = location.protocol, host = location.hostname;
          if (window.top !== window) {
            const origins = location.ancestorOrigins;
            if (!origins || !origins.length) return;
            try { const top = new URL(origins[origins.length - 1]); protocol = top.protocol; host = top.hostname; } catch (e) { return; }
          }
        """;

    // Corre o script só quando a página de topo é deste site. O hostname vai como literal JSON.
    public static string? Guard(string host, string? script)
    {
        if (string.IsNullOrEmpty(script)) return null;
        return $$"""
            (() => {
            {{TopLocation}}
              if (host !== {{JsonSerializer.Serialize(host.ToLowerInvariant())}}) return;
            {{script}}
            })();
            """;
    }

    // Script comum a todas as páginas do separador, só nos protocolos indicados (os da página de topo).
    public static string ForProtocols(IReadOnlyList<string> protocols, string script) => $$"""
        (() => {
        {{TopLocation}}
          if (!{{JsonSerializer.Serialize(protocols)}}.includes(protocol)) return;
        {{script}}
        })();
        """;
}
