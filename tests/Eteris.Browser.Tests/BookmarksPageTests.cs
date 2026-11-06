using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class BookmarksPageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-bm-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Remove_And_Rename()
    {
        var store = new BookmarkStore(_path);
        store.Toggle("https://a.pt/", "A", DateTime.UtcNow);
        store.Toggle("https://b.pt/x", "B", DateTime.UtcNow);
        store.Rename("https://a.pt", "  Novo nome  ");
        store.Remove("https://b.pt/x");
        var all = store.All();
        Assert.Equal("Novo nome", Assert.Single(all).Title);
    }

    [Fact]
    public void Render_EscapesAndCarriesToken()
    {
        var html = BookmarksPage.Render([new Bookmark("https://a.pt/", "<b>A</b>")]);
        Assert.DoesNotContain("<b>A</b>", html);
        Assert.Contains(InternalPages.Token, html);
    }

    [Fact]
    public void BookmarksPage_CanOpenFromCommandLine()
        => Assert.Equal("eteris://bookmarks/", Eteris.Browser.Infrastructure.StartupArgs.InitialUrl(["eteris://bookmarks/"]));
}
