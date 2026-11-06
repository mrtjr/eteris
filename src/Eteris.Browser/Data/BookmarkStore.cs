using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

public sealed record Bookmark(string Url, string Title);

public sealed class BookmarkStore
{
    private readonly string _connectionString;

    public BookmarkStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS bookmarks(
                id INTEGER PRIMARY KEY,
                url TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL)
            """);
    }

    // Devolve true se ficou marcado como favorito, false se foi removido.
    public bool Toggle(string url, string title, DateTime nowUtc)
    {
        var key = Key(url);
        if (Contains(key))
        {
            Execute("DELETE FROM bookmarks WHERE url = $u", ("$u", key));
            return false;
        }
        Execute("INSERT INTO bookmarks(url, title, created_at) VALUES ($u, $t, $c)",
            ("$u", key), ("$t", title), ("$c", new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds()));
        return true;
    }

    public void Remove(string url) => Execute("DELETE FROM bookmarks WHERE url = $u", ("$u", Key(url)));

    public void Rename(string url, string title)
        => Execute("UPDATE bookmarks SET title = $t WHERE url = $u", ("$u", Key(url)), ("$t", title.Trim()));

    public bool Contains(string url) => Query("SELECT url, title FROM bookmarks WHERE url = $u", 1, ("$u", Key(url))).Count > 0;

    public IReadOnlyList<Bookmark> All(int limit = 200)
        => Query("SELECT url, title FROM bookmarks ORDER BY created_at DESC, id DESC LIMIT $l", limit, ("$l", limit));

    public IReadOnlyList<Bookmark> Search(string text, int limit)
    {
        var term = text.Trim();
        if (term.Length == 0) return [];
        var pattern = "%" + term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        return Query("""
            SELECT url, title FROM bookmarks
            WHERE url LIKE $p ESCAPE '\' OR title LIKE $p ESCAPE '\'
            ORDER BY created_at DESC LIMIT $l
            """, limit, ("$p", pattern), ("$l", limit));
    }

    // "https://github.com" e "https://github.com/" são o mesmo favorito.
    private static string Key(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority) + "/"
            : url;

    private List<Bookmark> Query(string sql, int limit, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var list = new List<Bookmark>();
        while (reader.Read() && list.Count < limit) list.Add(new Bookmark(reader.GetString(0), reader.GetString(1)));
        return list;
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
