using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// A casa das fixadas no session.json (SessionTabMeta.Home): grava-se só nas fixadas, lê-se de volta (também nos Espaços
// na reserva), uma sessão antiga fica com a casa no endereço restaurado, e um valor que não serve é substituído.
public sealed class PinnedHomeSessionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-session-casa-" + Guid.NewGuid() + ".json");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
        File.Delete(_path + ".bak");
        File.Delete(_path + ".corrupt");
    }

    private void Save(TabsViewModel vm)
    {
        var (groupIds, groups) = vm.GroupSnapshot();
        var (spaces, index) = vm.WorkspaceSnapshot();
        new SessionStore(_path).Save(vm.Tabs.Select(t => t.Address), vm.Tabs.IndexOf(vm.Active!), vm.Tabs.Select(t => t.Title),
            vm.Tabs.Select(t => t.IsPinned), groupIds, groups, null, vm.MetaSnapshot(), spaces, index);
    }

    private TabsViewModel Load()
    {
        var session = new SessionStore(_path).Load()!;
        var vm = new TabsViewModel();
        vm.Restore(session.Urls, session.ActiveIndex, session.Titles, session.Pinned, session.GroupIds, session.Groups, session.Metas);
        vm.RestoreWorkspaces(session.Spaces, session.SpaceIndex);
        return vm;
    }

    private TabsViewModel LoadJson(string json)
    {
        File.WriteAllText(_path, json);
        return Load();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheHome()
    {
        var vm = new TabsViewModel();
        vm.Restore(["https://exemplo.pt/", "https://b.pt/", "https://c.pt/"], 0);
        vm.TogglePin(vm.Tabs[0]);
        vm.Tabs[0].Address = "https://exemplo.pt/outra";
        var work = vm.NewWorkspace("Trabalho", activate: false);
        var stored = vm.Tabs[2];
        vm.TogglePin(stored);
        stored.Address = "https://c.pt/fora";
        Assert.True(vm.MoveToWorkspace(stored, work));
        Save(vm);

        var loaded = Load();
        Assert.Equal("https://exemplo.pt/", loaded.Tabs[0].Home);
        Assert.Equal("https://exemplo.pt/outra", loaded.Tabs[0].Address);
        Assert.True(loaded.Tabs[0].IsAwayFromHome);
        Assert.Null(loaded.Tabs[1].Home);
        var reserve = Assert.Single(loaded.TabsOf(loaded.Workspaces[1]));
        Assert.True(reserve.IsPinned);
        Assert.Equal("https://c.pt/", reserve.Home);
        Assert.True(reserve.IsAwayFromHome);
    }

    [Fact]
    public void OldSession_PinnedWithoutHome_TakesTheRestoredAddress()
    {
        var tab = LoadJson("""{"Urls":["https://a.pt/x"],"ActiveIndex":0,"Pinned":[true]}""").Tabs[0];
        Assert.Equal("https://a.pt/x", tab.Home);
        Assert.False(tab.IsAwayFromHome);
    }

    [Fact]
    public void Restore_IgnoresAHomeOnAnUnpinnedTab()
    {
        var tab = LoadJson("""{"Urls":["https://a.pt/x"],"ActiveIndex":0,"Pinned":[false],"Metas":[{"Home":"https://b.pt/"}]}""").Tabs[0];
        Assert.False(tab.IsPinned);
        Assert.Null(tab.Home);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x")]
    public void Restore_ReplacesAnInvalidHome(string home)
    {
        var tab = LoadJson("{\"Urls\":[\"https://a.pt/x\"],\"ActiveIndex\":0,\"Pinned\":[true],\"Metas\":[{\"Home\":\"" + home + "\"}]}").Tabs[0];
        Assert.Equal("https://a.pt/x", tab.Home);
    }

    [Fact]
    public void Save_NeverWritesUserInfoInTheHome()
    {
        var vm = new TabsViewModel();
        vm.Restore(["https://ana:segredo@exemplo.pt/"], 0);
        vm.TogglePin(vm.Tabs[0]);
        vm.Tabs[0].Address = "https://exemplo.pt/outra";
        Save(vm);
        var json = File.ReadAllText(_path);
        Assert.Contains("\"Home\":\"https://exemplo.pt/\"", json);
        Assert.DoesNotContain("segredo@", json);
    }

    [Fact]
    public void Meta_WithOnlyAHome_IsNotEmpty()
    {
        Assert.False(new SessionTabMeta(Home: "https://a.pt/").IsEmpty);
        Assert.True(SessionTabMeta.Empty.IsEmpty);
    }
}
