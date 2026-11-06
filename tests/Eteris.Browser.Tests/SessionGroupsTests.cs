using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public sealed class SessionGroupsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-session-groups-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Groups_RoundTripThroughTheSessionFile()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/", "https://b.pt/", "https://c.pt/"], 0, groupIds: [0, 0, -1],
            groups: [new SessionGroup("Trabalho", 3, true)]);
        var session = store.Load()!;
        Assert.Equal([0, 0, -1], session.GroupIds!);
        Assert.Equal(new SessionGroup("Trabalho", 3, true), Assert.Single(session.Groups!));
    }

    [Fact]
    public void OldSessionsWithoutGroups_StillLoad()
    {
        File.WriteAllText(_path, """{"Urls":["https://a.pt/"],"ActiveIndex":0}""");
        var session = new SessionStore(_path).Load()!;
        Assert.Single(session.Urls);
        Assert.True(session.GroupIds is null || session.GroupIds.All(g => g < 0));
    }

    [Fact]
    public void Restore_RebuildsGroups()
    {
        var vm = new TabsViewModel();
        vm.Restore(["https://a.pt/", "https://b.pt/", "https://c.pt/"], 2, groupIds: [0, 0, -1],
            groups: [new SessionGroup("Trabalho", 3, false)]);
        var group = Assert.Single(vm.Groups);
        Assert.Equal("Trabalho", group.Name);
        Assert.Equal(3, group.ColorIndex);
        Assert.True(vm.Tabs[0].IsGroupHead);
        Assert.Equal(2, vm.Tabs[0].GroupCount);
        Assert.Null(vm.Tabs[2].Group);
    }

    [Fact]
    public void Snapshot_DescribesCurrentGroups()
    {
        var vm = new TabsViewModel();
        vm.NewTab("https://a.pt/");
        vm.NewTab("https://b.pt/");
        var group = vm.NewGroup(vm.Tabs[1]);
        group.Name = "Leitura";
        var (ids, groups) = vm.GroupSnapshot();
        Assert.Equal([-1, 0], ids);
        Assert.Equal("Leitura", Assert.Single(groups).Name);
    }
}
