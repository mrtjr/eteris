using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class ReadingListTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-reading-" + Guid.NewGuid() + ".db");
    private static readonly DateTime T0 = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Add_IsNewestFirstAndUnread()
    {
        var store = new ReadingListStore(_path);
        store.Add("https://a.pt/", "A", T0);
        store.Add("https://b.pt/", "B", T0.AddMinutes(1));
        var items = new ReadingListStore(_path).All();
        Assert.Equal(["B", "A"], items.Select(i => i.Title));
        Assert.All(items, i => Assert.False(i.Read));
    }

    [Fact]
    public void AddingTwice_KeepsOneEntry()
    {
        var store = new ReadingListStore(_path);
        store.Add("https://a.pt/", "A", T0);
        store.Add("https://a.pt/", "A outra vez", T0.AddMinutes(5));
        Assert.Single(store.All());
    }

    [Fact]
    public void MarkRead_AndRemove()
    {
        var store = new ReadingListStore(_path);
        store.Add("https://a.pt/", "A", T0);
        store.Add("https://b.pt/", "B", T0);
        store.MarkRead("https://a.pt/", true);
        store.Remove("https://b.pt/");
        var item = Assert.Single(store.All());
        Assert.True(item.Read);
        Assert.Equal(0, store.UnreadCount());
    }

    [Fact]
    public void Contains_FindsSavedPages()
    {
        var store = new ReadingListStore(_path);
        store.Add("https://a.pt/x", "A", T0);
        Assert.True(store.Contains("https://a.pt/x"));
        Assert.False(store.Contains("https://a.pt/y"));
    }

    // O «Anular» de remover devolve o item lido como lido e com a data em que foi guardado.
    [Fact]
    public void Restore_KeepsDateAndReadState()
    {
        var store = new ReadingListStore(_path);
        store.Add("https://lido.pt/", "Lido", T0);
        store.Add("https://novo.pt/", "Novo", T0.AddDays(1));
        store.MarkRead("https://lido.pt/", true);
        var item = store.Item("https://lido.pt/")!;
        store.Remove("https://lido.pt/");
        Assert.True(store.Restore(item));
        var back = store.Item("https://lido.pt/")!;
        Assert.True(back.Read);
        Assert.Equal(T0, back.AddedUtc);
        Assert.Equal(["Novo", "Lido"], store.All().Select(i => i.Title));
        Assert.False(store.Restore(item));
    }
}
