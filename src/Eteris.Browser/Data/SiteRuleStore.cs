using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Domínios bloqueados pelo utilizador só num site (Observatório). Lidos uma vez para memória (Shields/SiteBlockRules).
public sealed class SiteRuleStore
{
    private readonly string _connectionString;

    public SiteRuleStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS site_rules(site TEXT NOT NULL, domain TEXT NOT NULL, PRIMARY KEY(site, domain))";
        create.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Site, string Domain)> All()
    {
        using var connection = Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT site, domain FROM site_rules ORDER BY site, domain";
        using var reader = read.ExecuteReader();
        var rules = new List<(string, string)>();
        while (reader.Read()) rules.Add((reader.GetString(0), reader.GetString(1)));
        return rules;
    }

    public void Add(string site, string domain) => Execute("INSERT OR IGNORE INTO site_rules(site, domain) VALUES ($s, $d)", site, domain);

    public void Remove(string site, string domain) => Execute("DELETE FROM site_rules WHERE site = $s AND domain = $d", site, domain);

    public void RemoveSite(string site) => Execute("DELETE FROM site_rules WHERE site = $s", site, null);

    private void Execute(string sql, string site, string? domain)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$s", site.ToLowerInvariant());
        if (domain is not null) command.Parameters.AddWithValue("$d", domain.ToLowerInvariant());
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
