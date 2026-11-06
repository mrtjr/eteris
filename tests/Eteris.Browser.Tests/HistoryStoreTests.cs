using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-hist-" + Guid.NewGuid() + ".db");
    private readonly HistoryStore _store;
    private static readonly DateTime T0 = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public HistoryStoreTests() => _store = new HistoryStore(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Search_FindsByUrlFragment()
    {
        _store.Record("https://github.com/anthropics", "Anthropic", T0);
        var hit = Assert.Single(_store.Search("anthrop", 8));
        Assert.Equal("https://github.com/anthropics", hit.Url);
        Assert.Equal("Anthropic", hit.Title);
    }

    [Fact]
    public void Search_FindsByTitleCaseInsensitive()
    {
        _store.Record("https://x.pt/1", "Receita de Bacalhau", T0);
        Assert.Single(_store.Search("bacalhau", 8));
    }

    [Fact]
    public void Search_RanksHostPrefixBeforeOtherMatches()
    {
        _store.Record("https://example.com/about-github", "Sobre", T0);
        _store.Record("https://example.com/about-github", "Sobre", T0.AddMinutes(1));
        _store.Record("https://github.com/", "GitHub", T0);
        Assert.Equal("https://github.com/", _store.Search("git", 8)[0].Url);
    }

    [Fact]
    public void Search_RanksMoreVisitedFirstAmongEqualMatches()
    {
        _store.Record("https://a.pt/docs", "Docs A", T0);
        _store.Record("https://b.pt/docs", "Docs B", T0);
        _store.Record("https://b.pt/docs", "Docs B", T0.AddMinutes(1));
        Assert.Equal("https://b.pt/docs", _store.Search("docs", 8)[0].Url);
    }

    [Fact]
    public void Search_TreatsLikeWildcardsLiterally()
    {
        _store.Record("https://a.pt/x", "abc", T0);
        Assert.Empty(_store.Search("%", 8));
        Assert.Empty(_store.Search("_", 8));
    }

    [Fact]
    public void UpdateTitle_ChangesTitleOfLatestVisit()
    {
        _store.Record("https://a.pt/", "a.pt", T0);
        _store.UpdateTitle("https://a.pt/", "Página A");
        Assert.Equal("Página A", _store.Search("a.pt", 8)[0].Title);
    }

    [Fact]
    public void TopSites_GroupsByHostAndOrdersByVisits()
    {
        _store.Record("https://github.com/a", "A", T0);
        _store.Record("https://github.com/b", "B", T0.AddMinutes(1));
        _store.Record("https://www.github.com/c", "C", T0.AddMinutes(2));
        _store.Record("https://pt.wikipedia.org/", "Wikipédia", T0);
        var top = _store.TopSites(8);
        Assert.Equal(2, top.Count);
        Assert.Equal("github.com", top[0].Host);
        Assert.Equal(3, top[0].Visits);
        Assert.Equal("https://www.github.com/c", top[0].Url);
    }

    [Fact]
    public void Record_IgnoresNonWebSchemes()
    {
        _store.Record("eteris://newtab", "Nova aba", T0);
        _store.Record("about:blank", "", T0);
        Assert.Empty(_store.TopSites(8));
    }
}
