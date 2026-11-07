using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

public sealed record SnoozedTab(long Id, string Url, string Title, string? Name, string? Note, DateTime DueUtc);

// «Adiar até…»: abas que saíram da faixa e voltam a uma hora marcada (Tabs/Snooze.cs). Guardam o nome próprio e
// a nota para voltarem como estavam. Nunca na janela privada (o MainWindow nem chama o repositório).
public sealed class SnoozedTabsStore
{
    private readonly string _connectionString;

    public SnoozedTabsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS snoozed_tabs(
                id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, title TEXT NOT NULL, name TEXT, note TEXT, due_at INTEGER NOT NULL)
            """);
    }

    public void Add(string url, string title, string? name, string? note, DateTime dueUtc)
        => Execute("INSERT INTO snoozed_tabs(url, title, name, note, due_at) VALUES ($u, $t, $n, $o, $d)",
            ("$u", url), ("$t", title), ("$n", (object?)name ?? DBNull.Value), ("$o", (object?)note ?? DBNull.Value), ("$d", Unix(dueUtc)));

    public void Delete(long id) => Execute("DELETE FROM snoozed_tabs WHERE id = $i", ("$i", id));

    // Todas, da que volta primeiro para a que volta por último.
    public IReadOnlyList<SnoozedTab> All()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, url, title, name, note, due_at FROM snoozed_tabs ORDER BY due_at, id";
        using var reader = command.ExecuteReader();
        var rows = new List<SnoozedTab>();
        while (reader.Read())
            rows.Add(new SnoozedTab(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)).UtcDateTime));
        return rows;
    }

    public IReadOnlyList<SnoozedTab> Due(DateTime nowUtc) => Tabs.Snooze.Due(All(), s => s.DueUtc, nowUtc);

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
        return connection;
    }
}
