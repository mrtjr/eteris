using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class SavedSessionsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-sessions-" + Guid.NewGuid() + ".db");
    private static readonly DateTime T0 = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Save_RoundTripsTabsNewestFirst()
    {
        var store = new SavedSessionsStore(_path);
        store.Save("Trabalho", [new("https://a.pt/", "A"), new("https://b.pt/", "B")], T0);
        store.Save("Férias", [new("https://c.pt/", "C «x»")], T0.AddMinutes(1));
        var all = new SavedSessionsStore(_path).All();
        Assert.Equal(["Férias", "Trabalho"], all.Select(s => s.Name));
        Assert.Equal(["https://a.pt/", "https://b.pt/"], all[1].Tabs.Select(t => t.Url));
        Assert.Equal("C «x»", all[0].Tabs[0].Title);
        Assert.Equal(T0, all[1].CreatedUtc);
    }

    [Fact]
    public void Save_SameNameReplaces()
    {
        var store = new SavedSessionsStore(_path);
        store.Save("Trabalho", [new("https://a.pt/", "A")], T0);
        store.Save("Trabalho", [new("https://z.pt/", "Z")], T0.AddHours(1));
        var only = Assert.Single(store.All());
        Assert.Equal("https://z.pt/", only.Tabs.Single().Url);
    }

    [Fact]
    public void Delete_RemovesOnlyThatSession()
    {
        var store = new SavedSessionsStore(_path);
        store.Save("A", [new("https://a.pt/", "A")], T0);
        store.Save("B", [new("https://b.pt/", "B")], T0);
        store.Delete(store.All().Single(s => s.Name == "A").Id);
        Assert.Equal(["B"], store.All().Select(s => s.Name));
    }

    [Fact]
    public void Rename_RefusesANameAlreadyInUse()
    {
        var store = new SavedSessionsStore(_path);
        store.Save("A", [new("https://a.pt/", "A")], T0);
        store.Save("B", [new("https://b.pt/", "B")], T0);
        var a = store.All().Single(s => s.Name == "A");
        Assert.False(store.Rename(a.Id, "B"));
        Assert.True(store.Rename(a.Id, "Projeto"));
        Assert.Equal(["B", "Projeto"], store.All().Select(s => s.Name).Order());
    }

    [Fact]
    public void BookmarksPage_ListsSessionsSafely()
    {
        var html = Eteris.Browser.Browser.BookmarksPage.Render([], [],
            [new SavedSession(7, "<i>Projeto</i>", T0, [new("https://a.pt/", "A"), new("https://b.pt/", "B")])]);
        Assert.DoesNotContain("<i>Projeto</i>", html);
        Assert.Contains("\"id\":7", html);
        Assert.Contains("\"count\":2", html);
        Assert.Contains("tabSessions", html);
    }

    [Fact]
    public void Suggest_UsesMostCommonSiteAndCount()
    {
        SavedTab[] tabs = [new("https://x.pt/", ""), new("https://github.com/a", ""), new("https://www.github.com/b", "")];
        Assert.Equal("github.com e mais 2 abas", SavedSessionNames.Suggest(tabs, []));
        Assert.Equal("x.pt e mais 1 aba", SavedSessionNames.Suggest(tabs[..2], []));
        Assert.Equal("x.pt", SavedSessionNames.Suggest(tabs[..1], []));
    }

    [Fact]
    public void Suggest_AvoidsTakenNamesAndHandlesInternalPages()
    {
        SavedTab[] tabs = [new("https://a.pt/", "")];
        Assert.Equal("a.pt (3)", SavedSessionNames.Suggest(tabs, ["a.pt", "a.pt (2)"]));
        Assert.Equal("Sessão", SavedSessionNames.Suggest([new("eteris://history", "")], []));
    }
}
