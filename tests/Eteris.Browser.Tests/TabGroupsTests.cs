using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabGroupsTests
{
    private static TabsViewModel With(int count)
    {
        var vm = new TabsViewModel();
        for (var i = 0; i < count; i++) vm.NewTab($"https://s{i}.pt/");
        return vm;
    }

    [Fact]
    public void NewGroup_GetsNameColorAndHead()
    {
        var vm = With(3);
        var group = vm.NewGroup(vm.Tabs[1]);
        Assert.Equal("Grupo 1", group.Name);
        Assert.Same(group, vm.Tabs[1].Group);
        Assert.True(vm.Tabs[1].IsGroupHead);
        Assert.Equal(1, vm.Tabs[1].GroupCount);
        var second = vm.NewGroup(vm.Tabs[2]);
        Assert.NotEqual(group.ColorIndex, second.ColorIndex);
    }

    [Fact]
    public void AddToGroup_KeepsMembersTogether()
    {
        var vm = With(4);
        var a = vm.Tabs[0];
        var d = vm.Tabs[3];
        var group = vm.NewGroup(a);
        vm.AddToGroup(d, group);
        Assert.Same(d, vm.Tabs[1]);
        Assert.Equal(2, a.GroupCount);
        Assert.False(d.IsGroupHead);
    }

    [Fact]
    public void Collapse_HidesAllButTheHeadAndMovesActiveOut()
    {
        var vm = With(3);
        var group = vm.NewGroup(vm.Tabs[0]);
        vm.AddToGroup(vm.Tabs[1], group);
        vm.Active = vm.Tabs[1];
        vm.ToggleCollapse(group);
        Assert.True(group.IsCollapsed);
        Assert.False(vm.Tabs[0].IsHiddenByGroup);
        Assert.True(vm.Tabs[1].IsHiddenByGroup);
        Assert.Null(vm.Active!.Group);
    }

    [Fact]
    public void ActivatingATabInACollapsedGroup_ExpandsIt()
    {
        var vm = With(3);
        var group = vm.NewGroup(vm.Tabs[0]);
        vm.AddToGroup(vm.Tabs[1], group);
        vm.Active = vm.Tabs[2];
        vm.ToggleCollapse(group);
        vm.Active = vm.Tabs[1];
        Assert.False(group.IsCollapsed);
        Assert.False(vm.Tabs[1].IsHiddenByGroup);
    }

    [Fact]
    public void Ungroup_MovesTabAfterTheGroup()
    {
        var vm = With(4);
        var group = vm.NewGroup(vm.Tabs[0]);
        vm.AddToGroup(vm.Tabs[1], group);
        vm.AddToGroup(vm.Tabs[2], group);
        var middle = vm.Tabs[1];
        vm.Ungroup(middle);
        Assert.Null(middle.Group);
        Assert.Same(middle, vm.Tabs[2]);
        Assert.Equal(2, vm.Tabs[0].GroupCount);
    }

    [Fact]
    public void CloseGroup_ClosesMembersOnly()
    {
        var vm = With(3);
        var group = vm.NewGroup(vm.Tabs[0]);
        vm.AddToGroup(vm.Tabs[1], group);
        vm.CloseGroup(group);
        Assert.Single(vm.Tabs);
        Assert.Empty(vm.Groups);
    }

    [Fact]
    public void Dragging_IntoOrOutOfAGroup_UpdatesMembership()
    {
        var vm = With(4);
        var group = vm.NewGroup(vm.Tabs[0]);
        vm.AddToGroup(vm.Tabs[1], group);
        var outsider = vm.Tabs[3];
        vm.Move(outsider, 1);
        Assert.Same(group, outsider.Group);
        vm.Move(outsider, 3);
        Assert.Null(outsider.Group);
        Assert.Single(vm.Tabs, t => t.IsGroupHead);
    }

    [Fact]
    public void PinnedTabs_CannotBeGrouped()
    {
        var vm = With(2);
        vm.TogglePin(vm.Tabs[0]);
        vm.NewGroup(vm.Tabs[0]);
        Assert.Null(vm.Tabs[0].Group);
    }
}
