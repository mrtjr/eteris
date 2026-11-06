using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public sealed class BookmarkStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-bm-" + Guid.NewGuid() + ".db");
    private readonly BookmarkStore _store;
    private static readonly DateTime T0 = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    public BookmarkStoreTests() => _store = new BookmarkStore(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Toggle_AddsThenRemoves()
    {
        Assert.True(_store.Toggle("https://github.com/", "GitHub", T0));
        Assert.True(_store.Contains("https://github.com/"));
        Assert.False(_store.Toggle("https://github.com/", "GitHub", T0));
        Assert.False(_store.Contains("https://github.com/"));
    }

    [Fact]
    public void Contains_IgnoresTrailingSlash()
    {
        _store.Toggle("https://github.com", "GitHub", T0);
        Assert.True(_store.Contains("https://github.com/"));
    }

    [Fact]
    public void All_NewestFirst_AndPersists()
    {
        _store.Toggle("https://a.pt/", "A", T0);
        _store.Toggle("https://b.pt/", "B", T0.AddMinutes(1));
        var all = new BookmarkStore(_path).All();
        Assert.Equal(["https://b.pt/", "https://a.pt/"], all.Select(b => b.Url));
    }

    [Fact]
    public void Search_MatchesTitleOrUrl()
    {
        _store.Toggle("https://pt.wikipedia.org/wiki/Lisboa", "Lisboa – Wikipédia", T0);
        _store.Toggle("https://github.com/", "GitHub", T0);
        Assert.Single(_store.Search("lisboa", 5));
        Assert.Single(_store.Search("github", 5));
        Assert.Empty(_store.Search("", 5));
    }

    [Fact]
    public void Suggestions_BookmarksComeRightAfterPrimaryAndAreMarked()
    {
        var bookmarks = new[] { new HistoryEntry("https://github.com/anthropics", "Anthropic", 0) };
        var history = new[] { new HistoryEntry("https://github.com/x", "X", 5) };
        var list = Suggestions.Build("github", SearchEngine.DuckDuckGo, history, bookmarks);
        Assert.Equal(SuggestionKind.Bookmark, list[1].Kind);
        Assert.Equal(SuggestionKind.History, list[2].Kind);
    }

    [Fact]
    public void Suggestions_UrlInBothListsAppearsOnceAsBookmark()
    {
        var entry = new HistoryEntry("https://github.com/anthropics", "Anthropic", 3);
        var list = Suggestions.Build("anthropic", SearchEngine.DuckDuckGo, [entry], [entry]);
        Assert.Single(list, s => s.Url == entry.Url);
        Assert.Equal(SuggestionKind.Bookmark, list.Single(s => s.Url == entry.Url).Kind);
    }
}
