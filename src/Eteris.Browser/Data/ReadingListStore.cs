using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

public sealed record ReadingItem(string Url, string Title, DateTime AddedUtc, bool Read);

// «Ler mais tarde»: páginas guardadas para depois, com estado lido/por ler.
public sealed class ReadingListStore
{
    private readonly string _connectionString;

    public ReadingListStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS reading_list(
                url TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', added_at INTEGER NOT NULL, read INTEGER NOT NULL DEFAULT 0)
            """);
    }

    public void Add(string url, string title, DateTime nowUtc)
        => Execute("INSERT OR IGNORE INTO reading_list(url, title, added_at) VALUES ($u, $t, $a)",
            ("$u", url), ("$t", title), ("$a", new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds()));

    public void MarkRead(string url, bool read) => Execute("UPDATE reading_list SET read = $r WHERE url = $u", ("$u", url), ("$r", read ? 1 : 0));

    public void Remove(string url) => Execute("DELETE FROM reading_list WHERE url = $u", ("$u", url));

    public ReadingItem? Item(string url) => All().FirstOrDefault(i => i.Url == url);

    // O «Anular» de remover: a mesma data e o mesmo estado lido/por ler (false se já lá estava outra vez).
    public bool Restore(ReadingItem item)
    {
        if (Contains(item.Url)) return false;
        Execute("INSERT INTO reading_list(url, title, added_at, read) VALUES ($u, $t, $a, $r)",
            ("$u", item.Url), ("$t", item.Title), ("$a", new DateTimeOffset(DateTime.SpecifyKind(item.AddedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()),
            ("$r", item.Read ? 1 : 0));
        return true;
    }

    public bool Contains(string url)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM reading_list WHERE url = $u";
        command.Parameters.AddWithValue("$u", url);
        return command.ExecuteScalar() is not null;
    }

    public int UnreadCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reading_list WHERE read = 0";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<ReadingItem> All(int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT url, title, added_at, read FROM reading_list ORDER BY read, added_at DESC LIMIT {limit}";
        using var reader = command.ExecuteReader();
        var items = new List<ReadingItem>();
        while (reader.Read())
            items.Add(new ReadingItem(reader.GetString(0), reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).UtcDateTime, reader.GetInt64(3) == 1));
        return items;
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
