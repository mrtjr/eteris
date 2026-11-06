using Eteris.Browser.Shields;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Sites onde o utilizador desligou o escudo. Guardado por site (eTLD+1) e mantido em memória: é lido em cada pedido.
public sealed class ShieldExceptionStore
{
    private readonly string _connectionString;
    private readonly HashSet<string> _sites = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public ShieldExceptionStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS shield_exceptions(host TEXT PRIMARY KEY)";
            create.ExecuteNonQuery();
        }
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT host FROM shield_exceptions";
        using var reader = read.ExecuteReader();
        while (reader.Read()) _sites.Add(reader.GetString(0));
    }

    public IReadOnlyList<string> All()
    {
        lock (_lock) return _sites.Order().ToList();
    }

    public bool Contains(string host)
    {
        lock (_lock) return _sites.Contains(Site(host));
    }

    public void Add(string host)
    {
        var site = Site(host);
        lock (_lock) _sites.Add(site);
        Execute("INSERT OR IGNORE INTO shield_exceptions(host) VALUES ($h)", site);
    }

    public void Remove(string host)
    {
        var site = Site(host);
        lock (_lock) _sites.Remove(site);
        Execute("DELETE FROM shield_exceptions WHERE host = $h", site);
    }

    private static string Site(string host) => TrackerBlocker.RegistrableDomain(host.ToLowerInvariant());

    private void Execute(string sql, string host)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
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
