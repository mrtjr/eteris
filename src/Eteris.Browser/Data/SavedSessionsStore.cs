using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// Nome próprio e nota são os da aba na altura de guardar (Tabs/TabIdentity.cs); sessões antigas não os têm.
public sealed record SavedTab(string Url, string Title, string? Name = null, string? Note = null);

public sealed record SavedSession(long Id, string Name, DateTime CreatedUtc, IReadOnlyList<SavedTab> Tabs);

// «Sessões guardadas»: um conjunto de abas com nome, para reabrir mais tarde numa janela nova
// (um projeto, uma pesquisa, a rotina da manhã…). Guardar com um nome que já existe substitui-o.
public sealed class SavedSessionsStore
{
    private readonly string _connectionString;

    public SavedSessionsStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS saved_sessions(
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, created_at INTEGER NOT NULL, tabs TEXT NOT NULL)
            """);
    }

    public void Save(string name, IReadOnlyList<SavedTab> tabs, DateTime nowUtc)
        => Execute("""
            INSERT INTO saved_sessions(name, created_at, tabs) VALUES ($n, $c, $t)
            ON CONFLICT(name) DO UPDATE SET created_at = excluded.created_at, tabs = excluded.tabs
            """,
            ("$n", name), ("$c", new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds()), ("$t", JsonSerializer.Serialize(tabs)));

    // Falso quando já há outra sessão com esse nome.
    public bool Rename(long id, string name)
    {
        try
        {
            Execute("UPDATE saved_sessions SET name = $n WHERE id = $i", ("$n", name), ("$i", id));
            return true;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return false; }
    }

    public void Delete(long id) => Execute("DELETE FROM saved_sessions WHERE id = $i", ("$i", id));

    public IReadOnlyList<SavedSession> All()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, created_at, tabs FROM saved_sessions ORDER BY created_at DESC, id DESC";
        using var reader = command.ExecuteReader();
        var sessions = new List<SavedSession>();
        while (reader.Read())
        {
            IReadOnlyList<SavedTab> tabs;
            try { tabs = JsonSerializer.Deserialize<List<SavedTab>>(reader.GetString(3)) ?? []; }
            catch (JsonException) { tabs = []; }
            sessions.Add(new SavedSession(reader.GetInt64(0), reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).UtcDateTime, tabs));
        }
        return sessions;
    }

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

public static class SavedSessionNames
{
    // Nome automático a partir do site mais presente: «github.com e mais 3 abas».
    public static string Suggest(IReadOnlyList<SavedTab> tabs, IReadOnlyCollection<string> taken)
    {
        var sites = tabs
            .Select(t => Uri.TryCreate(t.Url, UriKind.Absolute, out var u) && u.Host.Length > 0 && (u.Scheme == "http" || u.Scheme == "https")
                ? Shields.TrackerBlocker.RegistrableDomain(u.Host.ToLowerInvariant()) : null)
            .OfType<string>()
            .ToList();
        var top = sites.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => sites.IndexOf(g.Key)).FirstOrDefault()?.Key;
        var rest = tabs.Count - 1;
        var name = top is null ? "Sessão" : rest <= 0 ? top : $"{top} e mais {rest} {(rest == 1 ? "aba" : "abas")}";
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }
}
