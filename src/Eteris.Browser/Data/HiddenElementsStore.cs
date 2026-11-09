using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Elementos que o utilizador escondeu com o seletor («Esconder elemento»), por host.
public sealed class HiddenElementsStore
{
    private readonly string _connectionString;

    public HiddenElementsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("CREATE TABLE IF NOT EXISTS hidden_elements(host TEXT NOT NULL, selector TEXT NOT NULL, PRIMARY KEY (host, selector))");
    }

    public void Add(string host, string selector)
        => Execute("INSERT OR IGNORE INTO hidden_elements(host, selector) VALUES ($h, $s)", ("$h", host.ToLowerInvariant()), ("$s", selector));

    public IReadOnlyList<string> For(string host)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT selector FROM hidden_elements WHERE host = $h ORDER BY rowid";
        command.Parameters.AddWithValue("$h", host.ToLowerInvariant());
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    public IReadOnlyDictionary<string, int> Hosts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT host, COUNT(*) FROM hidden_elements GROUP BY host";
        using var reader = command.ExecuteReader();
        var hosts = new Dictionary<string, int>();
        while (reader.Read()) hosts[reader.GetString(0)] = (int)reader.GetInt64(1);
        return hosts;
    }

    public void Remove(string host, string selector)
        => Execute("DELETE FROM hidden_elements WHERE host = $h AND selector = $s", ("$h", host.ToLowerInvariant()), ("$s", selector));

    public void Clear(string host) => Execute("DELETE FROM hidden_elements WHERE host = $h", ("$h", host.ToLowerInvariant()));

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
