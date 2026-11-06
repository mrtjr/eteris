using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Zoom escolhido para cada host (nível do Chromium; 0 = 100 %). Mantido em memória: é lido a cada navegação.
public sealed class SiteZoomStore
{
    private readonly string _connectionString;
    private readonly Dictionary<string, double> _levels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public SiteZoomStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS site_zoom(host TEXT PRIMARY KEY, level REAL NOT NULL)";
            create.ExecuteNonQuery();
        }
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT host, level FROM site_zoom";
        using var reader = read.ExecuteReader();
        while (reader.Read()) _levels[reader.GetString(0)] = reader.GetDouble(1);
    }

    public int Count
    {
        get { lock (_lock) return _levels.Count; }
    }

    public double Get(string host)
    {
        lock (_lock) return _levels.TryGetValue(host, out var level) ? level : 0;
    }

    public void Set(string host, double level)
    {
        host = host.ToLowerInvariant();
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (Math.Abs(level) < 0.001)
        {
            lock (_lock) _levels.Remove(host);
            command.CommandText = "DELETE FROM site_zoom WHERE host = $h";
        }
        else
        {
            lock (_lock) _levels[host] = level;
            command.CommandText = "INSERT OR REPLACE INTO site_zoom(host, level) VALUES ($h, $l)";
            command.Parameters.AddWithValue("$l", level);
        }
        command.Parameters.AddWithValue("$h", host);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
