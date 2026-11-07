using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Rastreadores bloqueados por dia (só números, nunca sites) para o painel semanal da nova aba.
public sealed class DailyStatsStore
{
    private readonly string _connectionString;

    public DailyStatsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS shield_daily(day TEXT PRIMARY KEY, blocked INTEGER NOT NULL)";
        command.ExecuteNonQuery();
    }

    public void Add(DateTime day, int blocked)
    {
        if (blocked <= 0) return;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO shield_daily(day, blocked) VALUES ($d, $n)
            ON CONFLICT(day) DO UPDATE SET blocked = blocked + $n
            """;
        command.Parameters.AddWithValue("$d", Key(day));
        command.Parameters.AddWithValue("$n", blocked);
        command.ExecuteNonQuery();
    }

    // Os últimos 7 dias, do mais antigo para hoje.
    public int[] Week(DateTime today)
    {
        var counts = new int[7];
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT day, blocked FROM shield_daily WHERE day >= $from";
        command.Parameters.AddWithValue("$from", Key(today.AddDays(-6)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var day = DateTime.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var index = 6 - (today.Date - day).Days;
            if (index is >= 0 and < 7) counts[index] += (int)reader.GetInt64(1);
        }
        return counts;
    }

    public void Clear()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM shield_daily";
        command.ExecuteNonQuery();
    }

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
