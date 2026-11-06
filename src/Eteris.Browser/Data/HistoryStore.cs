using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

public sealed record HistoryEntry(string Url, string Title, int Visits);

public sealed record TopSite(string Host, string Url, string Title, int Visits);

public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS history(
                id INTEGER PRIMARY KEY,
                url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                visited_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_history_url ON history(url);
            CREATE INDEX IF NOT EXISTS ix_history_visited ON history(visited_at);
            """);
    }

    public void Record(string url, string title, DateTime visitedUtc)
    {
        if (!IsWeb(url)) return;
        Execute("INSERT INTO history(url, title, visited_at) VALUES ($u, $t, $v)",
            ("$u", url), ("$t", title), ("$v", new DateTimeOffset(visitedUtc).ToUnixTimeMilliseconds()));
    }

    public void UpdateTitle(string url, string title)
        => Execute("""
            UPDATE history SET title = $t
            WHERE id = (SELECT id FROM history WHERE url = $u ORDER BY visited_at DESC, id DESC LIMIT 1)
            """, ("$u", url), ("$t", title));

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
        return connection;
    }

    private static bool IsWeb(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : string.Empty;

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
}
