using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

public sealed record ArchivedTab(long Id, string Url, string Title, string? Name, DateTime LastActiveUtc, DateTime ArchivedUtc);

// «Arquivo»: abas que saíram da faixa por não serem visitadas há N dias (Tabs/ArchivePolicy.cs). Guardam-se
// três meses; repõem-se a partir do Radar. Nunca na janela privada (o MainWindow nem chama o repositório).
public sealed class ArchivedTabsStore
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(90);

    private readonly string _connectionString;

    public ArchivedTabsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS archived_tabs(
                id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, title TEXT NOT NULL, name TEXT,
                last_active_at INTEGER NOT NULL, archived_at INTEGER NOT NULL)
            """);
    }

    public void Add(string url, string title, string? name, DateTime lastActiveUtc, DateTime nowUtc)
    {
        Execute("INSERT INTO archived_tabs(url, title, name, last_active_at, archived_at) VALUES ($u, $t, $n, $l, $a)",
            ("$u", url), ("$t", title), ("$n", (object?)name ?? DBNull.Value), ("$l", Unix(lastActiveUtc)), ("$a", Unix(nowUtc)));
        Execute("DELETE FROM archived_tabs WHERE archived_at < $a", ("$a", Unix(nowUtc - Keep)));
    }

    public void Delete(long id) => Execute("DELETE FROM archived_tabs WHERE id = $i", ("$i", id));

    // A arquivada mais recentemente primeiro.
    public IReadOnlyList<ArchivedTab> All()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, url, title, name, last_active_at, archived_at FROM archived_tabs ORDER BY archived_at DESC, id DESC";
        using var reader = command.ExecuteReader();
        var rows = new List<ArchivedTab>();
        while (reader.Read())
            rows.Add(new ArchivedTab(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)).UtcDateTime));
        return rows;
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

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
        return connection;
    }
}
