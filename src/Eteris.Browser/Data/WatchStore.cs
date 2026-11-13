using System.Globalization;
using Eteris.Browser.Watching;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// As vigias (Watching/) e as leituras de preço, no eteris.db. Nunca na janela privada (AppData.AddWatch recusa-a).
//
// - As horas guardam-se em ticks UTC: DateTime.MaxValue (a agenda de uma vigia bloqueada ou sem pedaço) e
//   DateTime.MinValue («desde sempre» do Limpar dados) voltam exatos.
// - Os preços guardam-se como texto invariante («1299.90») e leem-se com a cultura invariante: um Windows em português
//   não os estraga.
// - Os ids não se reaproveitam (AUTOINCREMENT): a verificação atrasada de uma vigia apagada nunca cai numa vigia nova.
// - Uma falha não apaga a última leitura boa; Failures conta as falhas seguidas (WatchRules.IsFailure), como as regras e a
//   agenda esperam, e FailingSince guarda a hora da primeira.
// - As leituras de preço guardam-se 90 dias (contados da verificação mais recente); saem com a vigia.
public sealed class WatchStore
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(90);

    private const string Columns = """
        id, url, site, title, kind, selector, use_session, interval_min, target_price, paused, status, failures, last_block,
        created_at, last_check_at, next_check_at, last_text, last_price, currency, failing_since
        """;

    private readonly string _connectionString;

    public WatchStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS watches(
                id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, site TEXT NOT NULL, title TEXT NOT NULL,
                kind TEXT NOT NULL, selector TEXT, use_session INTEGER NOT NULL, interval_min INTEGER NOT NULL,
                target_price TEXT, paused INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL, failures INTEGER NOT NULL DEFAULT 0,
                last_block TEXT NOT NULL, created_at INTEGER NOT NULL, last_check_at INTEGER, next_check_at INTEGER NOT NULL,
                last_text TEXT, last_price TEXT, currency TEXT, failing_since INTEGER);
            CREATE TABLE IF NOT EXISTS watch_readings(watch_id INTEGER NOT NULL, at INTEGER NOT NULL, price TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS watch_readings_by_watch ON watch_readings(watch_id, at);
            """;
        command.ExecuteNonQuery();
    }

    // Uma vigia nova verifica-se já (NextCheckUtc = nowUtc), nunca com menos de 30 min de intervalo.
    public long Add(NewWatch watch, DateTime nowUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watches(url, site, title, kind, selector, use_session, interval_min, target_price, paused, status, failures,
                                last_block, created_at, next_check_at)
            VALUES ($url, $site, $title, $kind, $selector, $session, $interval, $target, 0, $status, 0, $block, $now, $now)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$url", watch.Url);
        command.Parameters.AddWithValue("$site", SiteOf(watch.Url));
        command.Parameters.AddWithValue("$title", watch.Title);
        command.Parameters.AddWithValue("$kind", watch.Kind.ToString());
        command.Parameters.AddWithValue("$selector", (object?)watch.Selector ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", watch.UseSession ? 1 : 0);
        command.Parameters.AddWithValue("$interval", Math.Max(watch.IntervalMinutes, WatchSchedule.MinIntervalMinutes));
        command.Parameters.AddWithValue("$target", Price(watch.TargetPrice));
        command.Parameters.AddWithValue("$status", WatchStatus.New.ToString());
        command.Parameters.AddWithValue("$block", PageBlock.None.ToString());
        command.Parameters.AddWithValue("$now", Ticks(nowUtc));
        return (long)command.ExecuteScalar()!;
    }

    // Todas, da mais antiga para a mais nova.
    public IReadOnlyList<PageWatch> All() => Query($"SELECT {Columns} FROM watches ORDER BY created_at, id");

    // As que a agenda deve visitar: não pausadas, nem bloqueadas nem sem pedaço (decisão 8), com a hora chegada.
    public IReadOnlyList<PageWatch> Due(DateTime nowUtc)
        => Query($"""
            SELECT {Columns} FROM watches
            WHERE paused = 0 AND status NOT IN ('{WatchStatus.Blocked}', '{WatchStatus.Missing}') AND next_check_at <= $now
            ORDER BY next_check_at, id
            """, ("$now", Ticks(nowUtc)));

    // As vigias de um endereço, em qualquer estado (as bloqueadas incluídas): a leitura passiva lê-as quando o
    // utilizador abre a página.
    public IReadOnlyList<PageWatch> ForUrl(string url)
        => Query($"SELECT {Columns} FROM watches WHERE url = $url ORDER BY created_at, id", ("$url", url));

    // Grava uma verificação; false se a vigia já não existe (apagada a meio da visita): nada se grava e nada volta.
    public bool Record(long id, WatchCheck check, DateTime nextUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var failed = WatchRules.IsFailure(check);
        var at = Ticks(check.AtUtc);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE watches SET
                    status = $status, last_block = $block, last_check_at = $at, next_check_at = $next,
                    failures = CASE WHEN $failed THEN failures + 1 ELSE 0 END,
                    failing_since = CASE WHEN $failed THEN COALESCE(failing_since, $at) ELSE NULL END,
                    last_price = COALESCE($price, last_price), currency = COALESCE($currency, currency),
                    last_text = COALESCE($text, last_text)
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$status", check.Status.ToString());
            update.Parameters.AddWithValue("$block", check.Block.ToString());
            update.Parameters.AddWithValue("$at", at);
            update.Parameters.AddWithValue("$next", Ticks(nextUtc));
            update.Parameters.AddWithValue("$failed", failed ? 1 : 0);
            update.Parameters.AddWithValue("$price", Price(check.Price));
            update.Parameters.AddWithValue("$currency", (object?)check.Currency ?? DBNull.Value);
            update.Parameters.AddWithValue("$text", (object?)check.Text ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() == 0) return false;
        }

        if (check.Price is { } price)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO watch_readings(watch_id, at, price) VALUES ($id, $at, $price)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$at", at);
            insert.Parameters.AddWithValue("$price", Price(price));
            insert.ExecuteNonQuery();
        }

        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM watch_readings WHERE watch_id = $id AND at < $oldest";
            prune.Parameters.AddWithValue("$id", id);
            prune.Parameters.AddWithValue("$oldest", check.AtUtc - DateTime.MinValue > Keep ? Ticks(check.AtUtc - Keep) : 0L);
            prune.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    public void SetPaused(long id, bool paused)
        => Execute("UPDATE watches SET paused = $p WHERE id = $id", ("$p", paused ? 1 : 0), ("$id", id));

    public void Delete(long id)
        => Execute("DELETE FROM watch_readings WHERE watch_id = $id; DELETE FROM watches WHERE id = $id", ("$id", id));

    // Os preços lidos (até 90 dias), do mais antigo para o mais novo.
    public IReadOnlyList<(DateTime AtUtc, decimal Price)> Prices(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT at, price FROM watch_readings WHERE watch_id = $id ORDER BY at, rowid";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var rows = new List<(DateTime, decimal)>();
        while (reader.Read()) rows.Add((FromTicks(reader.GetInt64(0)), decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
        return rows;
    }

    // «Limpar dados › histórico»: as leituras do período saem; as vigias ficam (são ferramentas, como os favoritos).
    public int DeleteReadingsSince(DateTime sinceUtc)
        => Execute("DELETE FROM watch_readings WHERE at >= $since", ("$since", Ticks(sinceUtc)));

    // «Esquecer este site»: as vigias do site e dos seus subdomínios (SiteScope, pelo endereço), com as leituras.
    public int DeleteSite(string site)
    {
        using var connection = Open();
        connection.CreateFunction<string, string?>("eteris_site", SiteScope.SiteOf, isDeterministic: true);
        using var transaction = connection.BeginTransaction();
        using (var readings = connection.CreateCommand())
        {
            readings.Transaction = transaction;
            readings.CommandText = "DELETE FROM watch_readings WHERE watch_id IN (SELECT id FROM watches WHERE eteris_site(url) = $s)";
            readings.Parameters.AddWithValue("$s", site);
            readings.ExecuteNonQuery();
        }
        using var watches = connection.CreateCommand();
        watches.Transaction = transaction;
        watches.CommandText = "DELETE FROM watches WHERE eteris_site(url) = $s";
        watches.Parameters.AddWithValue("$s", site);
        var removed = watches.ExecuteNonQuery();
        transaction.Commit();
        return removed;
    }

    private static string SiteOf(string url)
        => SiteScope.SiteOf(url) ?? (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "");

    private static object Price(decimal? price) => price is { } p ? p.ToString(CultureInfo.InvariantCulture) : DBNull.Value;

    private static decimal? Price(SqliteDataReader reader, int column)
        => reader.IsDBNull(column) ? null : decimal.Parse(reader.GetString(column), CultureInfo.InvariantCulture);

    private static long Ticks(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).Ticks;

    private static DateTime FromTicks(long ticks) => new(ticks, DateTimeKind.Utc);

    private static DateTime? FromTicks(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : FromTicks(reader.GetInt64(column));

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private IReadOnlyList<PageWatch> Query(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var rows = new List<PageWatch>();
        while (reader.Read())
            rows.Add(new PageWatch(
                Id: reader.GetInt64(0), Url: reader.GetString(1), Site: reader.GetString(2), Title: reader.GetString(3),
                Kind: Enum.Parse<WatchKind>(reader.GetString(4)), Selector: Text(reader, 5), UseSession: reader.GetInt64(6) != 0,
                IntervalMinutes: reader.GetInt32(7), TargetPrice: Price(reader, 8), Paused: reader.GetInt64(9) != 0,
                Status: Enum.Parse<WatchStatus>(reader.GetString(10)), Failures: reader.GetInt32(11), CreatedUtc: FromTicks(reader.GetInt64(13)),
                LastCheckUtc: FromTicks(reader, 14), NextCheckUtc: FromTicks(reader.GetInt64(15)), LastText: Text(reader, 16),
                LastPrice: Price(reader, 17), Currency: Text(reader, 18), FailingSinceUtc: FromTicks(reader, 19),
                LastBlock: Enum.Parse<PageBlock>(reader.GetString(12))));
        return rows;
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqliteSetup.SecureDelete(connection);
        return connection;
    }
}
