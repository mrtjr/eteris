using System.Globalization;
using Eteris.Browser.Shields;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Agregados do relatório de privacidade: (dia, categoria, domínio do rastreador) → contagem. Sem sites visitados nem
// endereços; podados a PrivacyReport.KeepDays dias e apagáveis num clique (eteris://privacy/).
public sealed class PrivacyEventsStore
{
    private readonly string _connectionString;

    public PrivacyEventsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS privacy_events(day TEXT NOT NULL, category TEXT NOT NULL, subject TEXT NOT NULL,
                count INTEGER NOT NULL, PRIMARY KEY(day, category, subject))
            """;
        command.ExecuteNonQuery();
    }

    public void Add(DateOnly day, IReadOnlyList<(string Category, string Subject, int Count)> counts)
    {
        if (counts.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (category, subject, count) in counts)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO privacy_events(day, category, subject, count) VALUES ($d, $c, $s, $n)
                ON CONFLICT(day, category, subject) DO UPDATE SET count = count + $n
                """;
            command.Parameters.AddWithValue("$d", Key(day));
            command.Parameters.AddWithValue("$c", category);
            command.Parameters.AddWithValue("$s", subject);
            command.Parameters.AddWithValue("$n", count);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<PrivacyEvent> Since(DateOnly from)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT day, category, subject, count FROM privacy_events WHERE day >= $from";
        command.Parameters.AddWithValue("$from", Key(from));
        using var reader = command.ExecuteReader();
        var events = new List<PrivacyEvent>();
        while (reader.Read())
            events.Add(new PrivacyEvent(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetString(1), reader.GetString(2), (int)reader.GetInt64(3)));
        return events;
    }

    // Tira o que for mais antigo do que o prazo.
    public void Prune(DateOnly today) => Execute("DELETE FROM privacy_events WHERE day < $d", Key(today.AddDays(-PrivacyReport.KeepDays)));

    public void Clear() => Execute("DELETE FROM privacy_events", null);

    private void Execute(string sql, string? day)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (day is not null) command.Parameters.AddWithValue("$d", day);
        command.ExecuteNonQuery();
    }

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
