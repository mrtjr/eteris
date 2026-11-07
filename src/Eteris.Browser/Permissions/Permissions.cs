using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Permissions;

public enum SitePermission { Camera, Microphone, ScreenShare, Location, Notifications, Clipboard, MultipleDownloads, LocalNetwork }

public enum PermissionDecision { Allow, Deny, Ask }

public static class PermissionText
{
    private static readonly IReadOnlyDictionary<SitePermission, string> Phrases = new Dictionary<SitePermission, string>
    {
        [SitePermission.Camera] = "usar a câmara",
        [SitePermission.Microphone] = "usar o microfone",
        [SitePermission.ScreenShare] = "partilhar o seu ecrã",
        [SitePermission.Location] = "saber a sua localização",
        [SitePermission.Notifications] = "mostrar notificações",
        [SitePermission.Clipboard] = "ler a área de transferência",
        [SitePermission.MultipleDownloads] = "transferir vários ficheiros",
        [SitePermission.LocalNetwork] = "falar com a sua rede local e com este computador",
    };

    public static string Name(SitePermission kind) => kind switch
    {
        SitePermission.Camera => "Câmara",
        SitePermission.Microphone => "Microfone",
        SitePermission.ScreenShare => "Partilha de ecrã",
        SitePermission.Location => "Localização",
        SitePermission.Notifications => "Notificações",
        SitePermission.Clipboard => "Área de transferência",
        SitePermission.MultipleDownloads => "Várias transferências",
        SitePermission.LocalNetwork => "Rede local",
        _ => kind.ToString(),
    };

    public static string Describe(IReadOnlyCollection<SitePermission> kinds)
    {
        var ordered = kinds.Distinct().Order().ToList();
        var phrases = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            // "usar a câmara e o microfone": dois "usar" seguidos juntam-se.
            if (ordered[i] == SitePermission.Microphone && i > 0 && ordered[i - 1] == SitePermission.Camera)
                phrases[^1] += " e o microfone";
            else
                phrases.Add(Phrases[ordered[i]]);
        }
        return phrases.Count <= 1 ? phrases.FirstOrDefault() ?? string.Empty
            : string.Join(", ", phrases[..^1]) + " e " + phrases[^1];
    }
}

public static class PermissionRules
{
    public static string Site(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
            : origin;

    // Pedidos só de notificações são recusados sem perguntar quando o utilizador assim o quer (é o padrão).
    public static bool AutoDeny(IReadOnlyCollection<SitePermission> kinds, bool blockNotificationPrompts)
        => blockNotificationPrompts && kinds.Count > 0 && kinds.All(k => k == SitePermission.Notifications);

    public static PermissionDecision Decide(SitePermissionStore store, string site, IReadOnlyCollection<SitePermission> kinds)
    {
        if (kinds.Count == 0) return PermissionDecision.Deny;
        var remembered = kinds.Select(k => store.Get(site, k)).ToList();
        if (remembered.Any(r => r == false)) return PermissionDecision.Deny;
        return remembered.All(r => r == true) ? PermissionDecision.Allow : PermissionDecision.Ask;
    }
}

public sealed class SitePermissionStore
{
    private readonly string _connectionString;

    // Na janela privada nada vai para o perfil: as decisões ficam numa base só em memória, que acaba com a janela.
    private readonly SqliteConnection? _keepAlive;

    public static SitePermissionStore InMemory() => new(new SqliteConnectionStringBuilder
    {
        DataSource = "eteris-permissoes-" + Guid.NewGuid().ToString("N"),
        Mode = SqliteOpenMode.Memory,
        Cache = SqliteCacheMode.Shared,
    }.ToString(), keepAlive: true);

    public SitePermissionStore(string path)
        : this(new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString(), keepAlive: false)
    {
    }

    private SitePermissionStore(string connectionString, bool keepAlive)
    {
        _connectionString = connectionString;
        // Uma base em memória partilhada vive enquanto houver uma ligação aberta.
        if (keepAlive)
        {
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        Execute("""
            CREATE TABLE IF NOT EXISTS site_permissions(
                site TEXT NOT NULL, kind TEXT NOT NULL, allowed INTEGER NOT NULL,
                PRIMARY KEY (site, kind))
            """);
    }

    // As respostas de Get em memória, partilhadas pelas lojas da mesma base (cada janela tem a sua, e o AppData outra) e
    // esquecidas a cada escrita em qualquer delas: a rede local pergunta por cada pedido travado, na thread de rede.
    // A geração impede que uma leitura feita antes de uma escrita fique guardada depois dela.
    private sealed class Answers
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(string, SitePermission), bool?> Map = new();
        public int Generation;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Answers> Shared = new(StringComparer.Ordinal);

    private Answers Known => Shared.GetOrAdd(_connectionString, _ => new Answers());

    public bool? Get(string site, SitePermission kind)
    {
        var known = Known;
        if (known.Map.TryGetValue((site, kind), out var answer)) return answer;
        var generation = Volatile.Read(ref known.Generation);
        var value = Read(site, kind);
        if (Volatile.Read(ref known.Generation) == generation)
        {
            if (known.Map.Count >= 4096) known.Map.Clear();
            known.Map[(site, kind)] = value;
        }
        return value;
    }

    private bool? Read(string site, SitePermission kind)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT allowed FROM site_permissions WHERE site = $s AND kind = $k";
        command.Parameters.AddWithValue("$s", site);
        command.Parameters.AddWithValue("$k", kind.ToString());
        return command.ExecuteScalar() is long value ? value == 1 : null;
    }

    public IReadOnlyList<(SitePermission Kind, bool Allowed)> ForSite(string site)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, allowed FROM site_permissions WHERE site = $s";
        command.Parameters.AddWithValue("$s", site);
        using var reader = command.ExecuteReader();
        var list = new List<(SitePermission, bool)>();
        while (reader.Read())
            if (Enum.TryParse<SitePermission>(reader.GetString(0), out var kind)) list.Add((kind, reader.GetInt64(1) == 1));
        return list;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<(SitePermission Kind, bool Allowed)>> AllSites()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT site, kind, allowed FROM site_permissions ORDER BY site";
        using var reader = command.ExecuteReader();
        var sites = new Dictionary<string, List<(SitePermission, bool)>>();
        while (reader.Read())
        {
            if (!Enum.TryParse<SitePermission>(reader.GetString(1), out var kind)) continue;
            var site = reader.GetString(0);
            if (!sites.TryGetValue(site, out var list)) sites[site] = list = [];
            list.Add((kind, reader.GetInt64(2) == 1));
        }
        return sites.ToDictionary(p => p.Key, p => (IReadOnlyList<(SitePermission, bool)>)p.Value);
    }

    public void Set(string site, SitePermission kind, bool allowed)
        => Execute("INSERT OR REPLACE INTO site_permissions(site, kind, allowed) VALUES ($s, $k, $a)",
            ("$s", site), ("$k", kind.ToString()), ("$a", allowed ? 1 : 0));

    public void Forget(string site, SitePermission kind)
        => Execute("DELETE FROM site_permissions WHERE site = $s AND kind = $k", ("$s", site), ("$k", kind.ToString()));

    public void Forget(string site) => Execute("DELETE FROM site_permissions WHERE site = $s", ("$s", site));

    // A base em memória (janela privada) acaba aqui.
    public void Close()
    {
        _keepAlive?.Dispose();
        if (_keepAlive is not null) Shared.TryRemove(_connectionString, out _);
    }

    // Todas as chaves (host[:porta]) que pertencem a um site — subdomínios e portas incluídos; devolve quantas saíram.
    public int ForgetWhere(Func<string, bool> belongs)
    {
        var keys = AllSites().Keys.Where(belongs).ToList();
        foreach (var key in keys) Forget(key);
        return keys.Count;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }
        finally
        {
            var known = Known;
            Interlocked.Increment(ref known.Generation);
            known.Map.Clear();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
