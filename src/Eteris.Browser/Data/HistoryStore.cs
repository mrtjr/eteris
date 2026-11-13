using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

public sealed record HistoryEntry(string Url, string Title, int Visits);

public sealed record Visit(long Id, string Url, string Title, DateTime VisitedUtc);

public sealed record TopSite(string Host, string Url, string Title, int Visits);

public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        // Registo antecipado (WAL): uma escrita deixa de reescrever o diário inteiro com fsync a cada visita (medido:
        // 2,0 → 0,4 ms por INSERT). Fica guardado no próprio ficheiro, para todas as tabelas do Éteris.
        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS history(
                id INTEGER PRIMARY KEY,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                visited_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_history_url ON history(url);
            CREATE INDEX IF NOT EXISTS ix_history_visited ON history(visited_at);
            CREATE TABLE IF NOT EXISTS site_icons(
                site TEXT PRIMARY KEY NOT NULL,
                png BLOB NOT NULL);
            """);
    }

    // Credenciais embutidas no endereço (https://utilizador:senha@site/) nunca entram no histórico — e as chaves por
    // site continuam a bater com as visitas.
    internal static string WithoutUserInfo(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped)
            : url;

    public void Record(string url, string title, DateTime visitedUtc)
    {
        url = WithoutUserInfo(url);
        if (!IsWeb(url)) return;
        Execute("INSERT INTO history(url, title, visited_at) VALUES ($u, $t, $v)",
            ("$u", url), ("$t", title), ("$v", new DateTimeOffset(visitedUtc).ToUnixTimeMilliseconds()));
    }

    public void UpdateTitle(string url, string title)
        => Execute("""
            UPDATE history SET title = $t
            WHERE id = (SELECT id FROM history WHERE url = $u ORDER BY visited_at DESC, id DESC LIMIT 1)
            """, ("$u", WithoutUserInfo(url)), ("$t", title));

    public IReadOnlyList<HistoryEntry> Search(string text, int limit)
    {
        var term = text.Trim();
        if (term.Length == 0) return [];
        var pattern = "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var rows = Aggregate("WHERE url LIKE $p ESCAPE '\\' OR title LIKE $p ESCAPE '\\'", 50, ("$p", pattern));

        var bare = StripWww(term.ToLowerInvariant());
        return rows
            .OrderBy(r => StripWww(HostOf(r.Url)).StartsWith(bare, StringComparison.Ordinal) ? 0 : 1)
            .Take(limit)
            .Select(r => new HistoryEntry(r.Url, r.Title, r.Visits))
            .ToList();
    }

    public IReadOnlyList<Visit> Visits(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, url, title, visited_at FROM history ORDER BY visited_at DESC, id DESC LIMIT {limit}";
        using var reader = command.ExecuteReader();
        var visits = new List<Visit>();
        while (reader.Read())
            visits.Add(new Visit(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).UtcDateTime));
        return visits;
    }

    // As visitas mais recentes (até limit) que passam no filtro, procuradas no histórico inteiro — não só nas últimas
    // N visitas. O filtro corre em código gerido (as regras do filtro das abas: todas as palavras, sem acentos, que o
    // LIKE do SQLite não sabe) como função do SQLite, linha a linha, parando ao chegar ao limite.
    public IReadOnlyList<Visit> VisitsMatching(Func<string, string, bool> matches, int limit)
    {
        using var connection = Open();
        connection.CreateFunction<string, string, bool>("eteris_matches", (title, url) => matches(title ?? string.Empty, url ?? string.Empty));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, url, title, visited_at FROM history WHERE eteris_matches(title, url) ORDER BY visited_at DESC, id DESC LIMIT $l";
        command.Parameters.AddWithValue("$l", limit);
        using var reader = command.ExecuteReader();
        var visits = new List<Visit>();
        while (reader.Read())
            visits.Add(new Visit(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).UtcDateTime));
        return visits;
    }

    // O PNG reencodado e reduzido é guardado só depois de existir uma visita ao endereço.
    public void SaveSiteIcon(string url, byte[] png)
    {
        if (SiteIconKey(url) is not { } site || png.Length > 8192
            || !png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return;
        Execute("""
            INSERT INTO site_icons(site, png)
            SELECT $s, $p WHERE EXISTS (SELECT 1 FROM history WHERE url = $u)
            ON CONFLICT(site) DO UPDATE SET png = excluded.png WHERE site_icons.png <> excluded.png
            """, ("$s", site), ("$p", png), ("$u", url));
    }

    public IReadOnlyDictionary<string, string> SiteIcons(IEnumerable<string> urls)
    {
        var sites = urls.Select(SiteIconKey).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sites.Length == 0) return icons;

        using var connection = Open();
        foreach (var batch in sites.Chunk(250))
        {
            using var command = connection.CreateCommand();
            var placeholders = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders[i] = "$s" + i;
                command.Parameters.AddWithValue(placeholders[i], batch[i]);
            }
            command.CommandText = "SELECT site, png FROM site_icons WHERE site IN (" + string.Join(",", placeholders) + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                icons.Add(reader.GetString(0), "data:image/png;base64," + Convert.ToBase64String((byte[])reader.GetValue(1)));
        }
        return icons;
    }

    internal static string? SiteIconKey(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : null;

    // Um ícone fica órfão quando não resta nenhuma visita ao seu site. O índice ix_history_url
    // restringe os candidatos pelo prefixo do endereço (site <= url < site || '0' cobre "https://a.pt"
    // e "https://a.pt/…"; o motor guarda o host em minúsculas, como a chave) e a função gerida
    // site_icon_key só corre nessas poucas linhas, nunca no histórico inteiro — estas remoções
    // correm na thread de I/O do CEF e na de UI. Públicos para os testes verificarem o plano.
    internal const string DeleteIconOfLastVisitSql = """
        DELETE FROM site_icons
        WHERE site = (SELECT site_icon_key(url) FROM history WHERE id = $i)
          AND NOT EXISTS (SELECT 1 FROM history h
                          WHERE h.url >= site_icons.site AND h.url < site_icons.site || '0'
                            AND h.id <> $i AND site_icon_key(h.url) = site_icons.site)
        """;

    internal const string DeleteOrphanIconsSql = """
        DELETE FROM site_icons
        WHERE NOT EXISTS (SELECT 1 FROM history h
                          WHERE h.url >= site_icons.site AND h.url < site_icons.site || '0'
                            AND site_icon_key(h.url) = site_icons.site)
        """;

    public void DeleteVisit(long id)
        => DeleteHistory(DeleteIconOfLastVisitSql + "; DELETE FROM history WHERE id = $i;", ("$i", id));

    // Todas as visitas a um endereço num intervalo [desde, até) — a linha do histórico da barra lateral junta as
    // visitas repetidas do mesmo dia, e apagá-la tem de as levar todas (não só a mais recente).
    public void DeleteVisits(string url, DateTime fromUtc, DateTime toUtc)
        => DeleteHistory("DELETE FROM history WHERE url = $u AND visited_at >= $f AND visited_at < $t; " + DeleteOrphanIconsSql + ";",
            ("$u", url),
            ("$f", new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()),
            ("$t", new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()));

    // DateTime.MinValue apaga tudo.
    public void DeleteSince(DateTime sinceUtc)
    {
        if (sinceUtc == DateTime.MinValue)
        {
            DeleteHistory("DELETE FROM history; DELETE FROM site_icons;");
            return;
        }
        DeleteHistory("DELETE FROM history WHERE visited_at >= $v; " + DeleteOrphanIconsSql + ";",
            ("$v", new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()));
    }

    // «Triturar» e «Esquecer ao fechar»: as visitas a um site (domínio registável, ver SiteScope), em todas as datas.
    // O site tem de aparecer no texto do endereço (instr, em C no SQLite): só essas linhas passam pela função em C#,
    // em vez do histórico inteiro.
    private const string SiteWhere = "instr(lower(url), $s) > 0 AND eteris_site(url) = $s";

    public int CountSite(string site)
    {
        using var connection = Open();
        connection.CreateFunction<string, string?>("eteris_site", SiteScope.SiteOf, isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM history WHERE {SiteWhere}";
        command.Parameters.AddWithValue("$s", site);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void DeleteSite(string site) => TakeSite(site);

    // Apaga as visitas do site numa só passagem e devolve quantas eram e os endereços distintos (para saber que
    // origens apagar do armazenamento).
    public (int Count, IReadOnlyList<string> Urls) TakeSite(string site, int urlLimit = 500)
    {
        using var connection = Open();
        connection.CreateFunction<string, string?>("site_icon_key", SiteIconKey, isDeterministic: true);
        connection.CreateFunction<string, string?>("eteris_site", SiteScope.SiteOf, isDeterministic: true);
        var count = 0;
        var urls = new HashSet<string>(StringComparer.Ordinal);
        using (var transaction = connection.BeginTransaction())
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM history WHERE {SiteWhere} RETURNING url";
                command.Parameters.AddWithValue("$s", site);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    count++;
                    if (urls.Count < urlLimit) urls.Add(reader.GetString(0));
                }
            }
            using (var icons = connection.CreateCommand())
            {
                icons.Transaction = transaction;
                icons.CommandText = DeleteOrphanIconsSql + ";";
                icons.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        CheckpointWithRetry(connection);
        return (count, urls.ToList());
    }

    // Endereços distintos do site no histórico (para saber que origens apagar do armazenamento).
    public IReadOnlyList<string> SiteUrls(string site, int limit = 500)
    {
        using var connection = Open();
        connection.CreateFunction<string, string?>("eteris_site", SiteScope.SiteOf, isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT url FROM history WHERE {SiteWhere} LIMIT $l";
        command.Parameters.AddWithValue("$s", site);
        command.Parameters.AddWithValue("$l", limit);
        using var reader = command.ExecuteReader();
        var urls = new List<string>();
        while (reader.Read()) urls.Add(reader.GetString(0));
        return urls;
    }

    // As origens da web de todo o histórico, sem repetidas («Apagar dados › Cookies e dados de sites» apaga o
    // armazenamento de cada uma; recolhem-se antes de apagar o histórico).
    public IReadOnlyList<string> Origins()
    {
        using var connection = Open();
        connection.CreateFunction<string, string?>("eteris_origin", SiteScope.OriginOf, isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT eteris_origin(url) FROM history";
        using var reader = command.ExecuteReader();
        var origins = new List<string>();
        while (reader.Read())
            if (!reader.IsDBNull(0)) origins.Add(reader.GetString(0));
        return origins;
    }

    // Reescreve o ficheiro só com o que está vivo (VACUUM) e esvazia o -wal: o que versões anteriores apagaram sem
    // secure_delete deixa de se ler no ficheiro. Demora com um histórico grande: nunca na thread da janela.
    public bool Compact()
    {
        try
        {
            using var connection = Open();
            using (var vacuum = connection.CreateCommand())
            {
                vacuum.CommandText = "VACUUM;";
                vacuum.ExecuteNonQuery();
            }
            return Checkpoint(connection);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // A compactação para o que versões anteriores deixaram: corre uma só vez (o eteris.db não usa o user_version; o do
    // cofre é de outro ficheiro). Se a base estiver ocupada não marca, e repete na saída seguinte.
    public bool CompactOnce()
    {
        try
        {
            using (var connection = Open())
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt64(read.ExecuteScalar()) >= 1) return false;
            }
            if (!Compact()) return false;
            using var mark = Open();
            using var set = mark.CreateCommand();
            set.CommandText = "PRAGMA user_version = 1;";
            set.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // Passa o registo antecipado (-wal) para a base e esvazia-o. Devolve false quando a base estava ocupada (um leitor
    // ainda precisa do -wal) ou a espera de 1 s acabou.
    internal static bool Checkpoint(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.CommandTimeout = 1;
            using var reader = command.ExecuteReader();
            return reader.Read() && reader.GetInt32(0) == 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    // Até 3 tentativas, 50 ms entre elas, enquanto vier ocupado; se continuar ocupado segue: o checkpoint automático do
    // SQLite acaba o trabalho.
    private static void CheckpointWithRetry(SqliteConnection connection)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Checkpoint(connection)) return;
            if (attempt < 2) Thread.Sleep(50);
        }
    }

    private void DeleteHistory(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        connection.CreateFunction<string, string?>("site_icon_key", SiteIconKey, isDeterministic: true);
        connection.CreateFunction<string, string?>("eteris_site", SiteScope.SiteOf, isDeterministic: true);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
        transaction.Commit();
        // O que se apagou não fica à espera no ficheiro do registo antecipado (-wal): passa já para a base e o -wal esvazia.
        CheckpointWithRetry(connection);
    }

    public IReadOnlyList<TopSite> TopSites(int limit)
        => Aggregate(string.Empty, 500)
            .GroupBy(r => StripWww(HostOf(r.Url)))
            .Select(g =>
            {
                var latest = g.MaxBy(r => r.Last)!;
                return (Site: new TopSite(g.Key, latest.Url, latest.Title, g.Sum(r => r.Visits)), Last: latest.Last);
            })
            .OrderByDescending(s => s.Site.Visits)
            .ThenByDescending(s => s.Last)
            .Take(limit)
            .Select(s => s.Site)
            .ToList();

    private sealed record Row(string Url, string Title, int Visits, long Last);

    private List<Row> Aggregate(string where, int limit, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT url,
                   (SELECT title FROM history h2 WHERE h2.url = h.url ORDER BY visited_at DESC, id DESC LIMIT 1),
                   COUNT(*) AS visits, MAX(visited_at) AS last
            FROM history h {where}
            GROUP BY url ORDER BY visits DESC, last DESC LIMIT {limit}
            """;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
            rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)));
        return rows;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqliteSetup.SecureDelete(connection);
        // Com WAL, NORMAL só sincroniza o disco nos checkpoints: uma falha de energia pode perder as últimas visitas,
        // nunca corromper a base.
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool IsWeb(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
}
