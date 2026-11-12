using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-session-" + Guid.NewGuid() + ".json");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
    }

    // ARQ-02: um session.json só de leitura ou preso por outro programa não pode fechar o Éteris.
    [Fact]
    public void TrySave_WhenTheFileIsReadOnly_ReturnsFalseAndKeepsTheFile()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/"], 0);
        var before = File.ReadAllText(_path);
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(store.TrySave(["https://b.pt/"], 0));
            Assert.Equal(before, File.ReadAllText(_path));
            Assert.False(File.Exists(_path + ".tmp"));
        }
        finally { File.SetAttributes(_path, FileAttributes.Normal); }
    }

    [Fact]
    public void TrySave_WhenTheTemporaryPathIsADirectory_ReturnsFalse()
    {
        Directory.CreateDirectory(_path + ".tmp");
        try { Assert.False(new SessionStore(_path).TrySave(["https://a.pt/"], 0)); }
        finally { Directory.Delete(_path + ".tmp"); }
    }

    // DAD-13: entradas null no JSON (ficheiro editado à mão ou estragado) não fazem a leitura lançar.
    [Fact]
    public void Load_WithNullEntries_DoesNotThrow()
    {
        File.WriteAllText(_path, """{"Urls":["https://a.pt/",null],"ActiveIndex":0,"Titles":[null,null],"GroupIds":[0,0],"Groups":[null]}""");
        var session = new SessionStore(_path).Load()!;
        Assert.Equal(["https://a.pt/"], session.Urls);
        Assert.Equal([""], session.Titles);
        Assert.Equal("", Assert.Single(session.Groups!).Name);
        var vm = new Eteris.Browser.Tabs.TabsViewModel();
        vm.Restore(session.Urls, session.ActiveIndex, session.Titles, session.Pinned, session.GroupIds, session.Groups, session.Metas);
        Assert.Single(vm.Tabs);
    }

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
    public void SaveThenLoad_KeepsTitlesAlignedWithKeptUrls()
    {
        var store = new SessionStore(_path);
        store.Save(["javascript:x", "https://a.pt/", "https://b.pt/"], 2, ["X", "Página A", "Página B"]);
        var session = store.Load()!;
        Assert.Equal(["Página A", "Página B"], session.Titles);
    }

    [Fact]
    public void SaveThenLoad_KeepsPinnedFlags()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/", "https://b.pt/"], 1, ["A", "B"], [true, false]);
        Assert.Equal([true, false], store.Load()!.Pinned);
    }

    [Fact]
    public void SaveThenLoad_KeepsTheSplitWithItsOrientation()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/", "https://b.pt/", "https://c.pt/"], 0, split: new SessionSplit(2, 0, "stacked"));
        var split = store.Load()!.Split;
        Assert.NotNull(split);
        Assert.Equal((2, 0, "stacked"), (split.Left, split.Right, split.Orientation));
    }

    [Fact]
    public void Save_RemapsTheSplitWhenTabsAreDropped()
    {
        var store = new SessionStore(_path);
        store.Save(["javascript:x", "https://a.pt/", "about:blank", "https://b.pt/"], 1, split: new SessionSplit(1, 3, "side"));
        var split = store.Load()!.Split;
        Assert.Equal((0, 1), (split!.Left, split.Right));
    }

    [Fact]
    public void Save_DropsASplitWhoseTabDidNotSurvive()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/", "about:blank"], 0, split: new SessionSplit(0, 1, "side"));
        Assert.Null(store.Load()!.Split);
    }

    [Fact]
    public void Load_RejectsAnInvalidSplit()
    {
        File.WriteAllText(_path, """{"Urls":["https://a.pt/","https://b.pt/"],"ActiveIndex":0,"Split":{"Left":0,"Right":7,"Orientation":"side"}}""");
        Assert.Null(new SessionStore(_path).Load()!.Split);
        File.WriteAllText(_path, """{"Urls":["https://a.pt/","https://b.pt/"],"ActiveIndex":0,"Split":{"Left":1,"Right":1,"Orientation":null}}""");
        Assert.Null(new SessionStore(_path).Load()!.Split);
    }

    [Fact]
    public void Load_WithoutSplit_IsCompatibleWithOlderSessions()
    {
        File.WriteAllText(_path, """{"Urls":["https://a.pt/"],"ActiveIndex":0}""");
        var session = new SessionStore(_path).Load();
        Assert.NotNull(session);
        Assert.Null(session.Split);
    }

    [Theory]
    [InlineData(0, 1, 2, true)]
    [InlineData(1, 1, 2, false)]
    [InlineData(-1, 0, 2, false)]
    [InlineData(0, 2, 2, false)]
    public void SessionSplit_IsValid(int left, int right, int count, bool valid)
        => Assert.Equal(valid, new SessionSplit(left, right, "side").IsValid(count));

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
    // Antes cortava-se em 100 abas: as seguintes perdiam-se em silêncio ao gravar (na faixa e nos Espaços na reserva).
    [Fact]
    public void Save_KeepsEveryTab_InTheStripAndInReservedSpaces()
    {
        var urls = Enumerable.Range(0, 250).Select(i => $"https://site{i}.pt/").ToList();
        var titles = urls.Select((_, i) => $"Aba {i}").ToList();
        var reserved = Enumerable.Range(0, 180).Select(i => $"https://reserva{i}.pt/").ToList();
        var store = new SessionStore(_path);
        store.Save(urls, 230, titles, spaces:
            [new SessionSpace("Geral", 0), new SessionSpace("Trabalho", 1, Tabs: new Session(reserved, 179))], spaceIndex: 0);
        var session = new SessionStore(_path).Load()!;
        Assert.Equal(urls, session.Urls);
        Assert.Equal(titles, session.Titles);
        Assert.Equal(230, session.ActiveIndex);
        Assert.Equal(reserved, session.Spaces![1].Tabs!.Urls);
        Assert.Equal(179, session.Spaces[1].Tabs!.ActiveIndex);
    }

    // DAD-02: o session.json passa a levar todas as janelas normais (campo opcional Windows).
    private static SessionWindow Window(string[] urls, int active = 0, SessionTabMeta?[]? metas = null, WindowPlacement? placement = null)
        => new(SessionStore.Compose(urls, active, metas: metas), placement);

    [Fact]
    public void SaveWindows_TwoWindows_RoundTripsBoth()
    {
        var store = new SessionStore(_path);
        var second = new WindowPlacement(40, 60, 1200, 800, false);
        Assert.True(store.TrySaveWindows([
            Window(["https://a.pt/", "https://b.pt/", "https://c.pt/"], 1),
            Window(["https://d.pt/", "https://e.pt/"], 1, [null, new SessionTabMeta(Note: "códigos", Locked: true)], second)]));
        var session = store.Load()!;
        Assert.Equal(["https://a.pt/", "https://b.pt/", "https://c.pt/"], session.Urls);
        Assert.Equal(1, session.ActiveIndex);
        var other = Assert.Single(session.Windows!);
        Assert.Equal(["https://d.pt/", "https://e.pt/"], other.Tabs.Urls);
        Assert.True(other.Tabs.Metas![1].Locked);
        Assert.Equal("códigos", other.Tabs.Metas[1].Note);
        Assert.Equal(second, other.Placement);
        Assert.Null(other.Tabs.Windows);
        Assert.Single(System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(_path), StartupJsonContext.Default.Session)!.Windows!);
    }

    [Fact]
    public void Load_OldFormat_IsOneWindow()
    {
        File.WriteAllText(_path, """{"Urls":["https://a.pt/"],"ActiveIndex":0,"Titles":["A"]}""");
        var session = new SessionStore(_path).Load()!;
        Assert.Equal(["https://a.pt/"], session.Urls);
        Assert.Null(session.Windows);
    }

    [Fact]
    public void SaveWindows_OneWindow_WritesTheOldFormat()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/"], 0);
        var old = File.ReadAllText(_path);
        Assert.True(store.TrySaveWindows([Window(["https://a.pt/"])]));
        Assert.Equal(old, File.ReadAllText(_path));
        Assert.DoesNotContain("\"Windows\"", old);
    }

    [Fact]
    public void Load_DropsWindowsWithNothingToRestore_AndPromotesTheNext()
    {
        var store = new SessionStore(_path);
        store.TrySaveWindows([Window(["eteris://newtab/"]), Window(["https://d.pt/"]), Window(["javascript:x"])]);
        var session = store.Load()!;
        Assert.Equal(["https://d.pt/"], session.Urls);
        Assert.Null(session.Windows);
    }

    [Fact]
    public void SaveWindows_Empty_WritesNothing()
    {
        Assert.True(new SessionStore(_path).TrySaveWindows([]));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Load_WindowsWithNullEntries_AreSkipped()
    {
        File.WriteAllText(_path, """{"Urls":["https://a.pt/"],"ActiveIndex":0,"Windows":[null,{"Tabs":null},{"Tabs":{"Urls":null,"ActiveIndex":0}},{"Tabs":{"Urls":["https://d.pt/"],"ActiveIndex":5}}]}""");
        var session = new SessionStore(_path).Load()!;
        var other = Assert.Single(session.Windows!);
        Assert.Equal(["https://d.pt/"], other.Tabs.Urls);
        Assert.Equal(0, other.Tabs.ActiveIndex);
        Assert.Null(other.Placement);
    }
}
