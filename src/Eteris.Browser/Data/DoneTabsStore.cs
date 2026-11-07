using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

public sealed record DoneTab(long Id, string Url, string Title, DateTime DoneUtc);

// «Feito»: abas despachadas, com o endereço, o título e a hora, para a vista «Feitas hoje» do Radar. Guarda-se um
// mês e depois apaga-se sozinho; nunca na janela privada (o MainWindow nem chama o repositório).
public sealed class DoneTabsStore
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    private readonly string _connectionString;

    public DoneTabsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS done_tabs(
                id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, title TEXT NOT NULL, done_at INTEGER NOT NULL)
            """);
    }

    public void Add(string url, string title, DateTime nowUtc)
    {
        Execute("INSERT INTO done_tabs(url, title, done_at) VALUES ($u, $t, $d)",
            ("$u", url), ("$t", title), ("$d", Unix(nowUtc)));
        Execute("DELETE FROM done_tabs WHERE done_at < $d", ("$d", Unix(nowUtc - Keep)));
    }

    public void Delete(long id) => Execute("DELETE FROM done_tabs WHERE id = $i", ("$i", id));

    // Feitas desde um instante (normalmente o início do dia local), a mais recente primeiro.
    public IReadOnlyList<DoneTab> Since(DateTime sinceUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, url, title, done_at FROM done_tabs WHERE done_at >= $d ORDER BY done_at DESC, id DESC";
        command.Parameters.AddWithValue("$d", Unix(sinceUtc));
        using var reader = command.ExecuteReader();
        var rows = new List<DoneTab>();
        while (reader.Read())
            rows.Add(new DoneTab(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).UtcDateTime));
        return rows;
    }

    // Início do dia local de um instante, em UTC: o «hoje» de «Feitas hoje».
    public static DateTime StartOfLocalDayUtc(DateTime nowLocal) => nowLocal.Date.ToUniversalTime();

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
