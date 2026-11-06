using Eteris.Browser.Shields;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Sites em modo rigoroso (por eTLD+1), em memória porque é consultado a cada pedido.
public sealed class StrictSiteStore
{
    private readonly string _connectionString;
    private readonly HashSet<string> _sites = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private readonly string _table;

    // A tabela é uma constante interna (strict_sites, dark_sites), nunca texto vindo de fora.
    public StrictSiteStore(string path, string table = "strict_sites")
    {
        _table = table;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE IF NOT EXISTS {_table}(host TEXT PRIMARY KEY)";
            create.ExecuteNonQuery();
        }
        using var read = connection.CreateCommand();
        read.CommandText = $"SELECT host FROM {_table}";
        using var reader = read.ExecuteReader();
        while (reader.Read()) _sites.Add(reader.GetString(0));
    }

    public bool Contains(string host)
    {
        lock (_lock) return _sites.Contains(Site(host));
    }

    public IReadOnlyList<string> All()
    {
        lock (_lock) return _sites.Order().ToList();
    }

    public void Add(string host)
    {
        var site = Site(host);
        lock (_lock) _sites.Add(site);
        Execute($"INSERT OR IGNORE INTO {_table}(host) VALUES ($h)", site);
    }

    public void Remove(string host)
    {
        var site = Site(host);
        lock (_lock) _sites.Remove(site);
        Execute($"DELETE FROM {_table} WHERE host = $h", site);
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
