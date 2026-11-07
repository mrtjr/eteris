using System.IO;
using Eteris.Browser.Data;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Tests;

// Histórico fora da thread da janela (docs/motor.md, F5): registo antecipado (WAL), fila pela ordem e títulos juntos.
public sealed class HistoryWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-writer-" + Guid.NewGuid() + ".db");
    private readonly HistoryStore _store;
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    public HistoryWriterTests() => _store = new HistoryStore(_path);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Store_UsesWriteAheadLogging()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar() as string);
    }

    [Fact]
    public void QueuedWrites_KeepTheirOrder_AndFlushWaitsForThem()
    {
        using var writer = new HistoryWriter(() => _store, TimeSpan.FromSeconds(1));
        for (var i = 0; i < 50; i++)
        {
            var at = T0.AddSeconds(i);
            var url = $"https://site{i}.pt/";
            writer.Enqueue(h => h.Record(url, "t", at));
        }
        writer.Enqueue(h => h.DeleteSince(T0.AddSeconds(25)));
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));
        var visits = _store.Visits(100);
        Assert.Equal(25, visits.Count);
        Assert.Equal("https://site24.pt/", visits[0].Url);
    }

    [Fact]
    public void Titles_AreCoalesced_OnlyTheLastIsWritten()
    {
        using var writer = new HistoryWriter(() => _store, TimeSpan.FromMilliseconds(200));
        writer.Enqueue(h => h.Record("https://chat.pt/", "Chat", T0));
        for (var i = 1; i <= 20; i++) writer.UpdateTitleLater("https://chat.pt/", $"({i}) Chat");
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal("(20) Chat", _store.Visits(1)[0].Title);
        Assert.Equal(2, writer.Written); // a visita e um só título
    }

    [Fact]
    public void PendingTitle_IsWrittenAfterTheDelay_WithoutAFlush()
    {
        using var writer = new HistoryWriter(() => _store, TimeSpan.FromMilliseconds(100));
        writer.Enqueue(h => h.Record("https://a.pt/", "A", T0));
        writer.UpdateTitleLater("https://a.pt/", "A (novo)");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && _store.Visits(1).FirstOrDefault()?.Title != "A (novo)") Thread.Sleep(20);
        Assert.Equal("A (novo)", _store.Visits(1)[0].Title);
    }

    [Fact]
    public void Dispose_WritesWhatIsLeft()
    {
        var writer = new HistoryWriter(() => _store, TimeSpan.FromMinutes(5));
        writer.Enqueue(h => h.Record("https://b.pt/", "B", T0));
        writer.UpdateTitleLater("https://b.pt/", "B final");
        writer.Dispose();
        Assert.Equal("B final", _store.Visits(1)[0].Title);
    }

    [Fact]
    public void AFailingWrite_DoesNotStopTheQueue()
    {
        using var writer = new HistoryWriter(() => _store, TimeSpan.FromSeconds(1));
        writer.Enqueue(_ => throw new InvalidOperationException("base trancada"));
        writer.Enqueue(h => h.Record("https://c.pt/", "C", T0));
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));
        Assert.Single(_store.Visits(10));
    }
}
