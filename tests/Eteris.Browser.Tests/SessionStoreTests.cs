using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-session-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void SaveThenLoad_RoundTripsTabsAndActive()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/", "https://b.pt/x", "eteris://newtab/"], activeIndex: 1);
        var session = new SessionStore(_path).Load();
        Assert.NotNull(session);
        Assert.Equal(["https://a.pt/", "https://b.pt/x", "eteris://newtab/"], session.Urls);
        Assert.Equal(1, session.ActiveIndex);
    }

    [Fact]
    public void Load_WithoutFile_IsNull()
        => Assert.Null(new SessionStore(_path).Load());

    [Fact]
    public void Load_OnlyNewTabs_IsNull()
    {
        var store = new SessionStore(_path);
        store.Save(["eteris://newtab/", "eteris://newtab/"], 0);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_CorruptFile_IsNull()
    {
        File.WriteAllText(_path, "{ isto não é json");
        Assert.Null(new SessionStore(_path).Load());
    }

    [Fact]
    public void Save_DropsUnsafeSchemesAndClampsActive()
    {
        var store = new SessionStore(_path);
        store.Save(["javascript:alert(1)", "https://a.pt/", "data:text/html,x"], activeIndex: 7);
        var session = store.Load()!;
        Assert.Equal(["https://a.pt/"], session.Urls);
        Assert.Equal(0, session.ActiveIndex);
    }
}
