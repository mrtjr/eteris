using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabsViewModelTests
{
    private static TabsViewModel WithTabs(params string[] urls)
    {
        var vm = new TabsViewModel();
        foreach (var url in urls) vm.NewTab(url);
        return vm;
    }

    [Fact]
    public void NewTab_AppendsAndActivates()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt");
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal("https://b.pt", vm.Active!.Address);
    }

    [Fact]
    public void NewTab_InBackground_InsertsRightOfActiveWithoutActivating()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt");
        vm.Active = vm.Tabs[0];
        var opened = vm.NewTab("https://c.pt", activate: false);
        Assert.Same(opened, vm.Tabs[1]);
        Assert.Same(vm.Tabs[0], vm.Active);
    }

    [Fact]
    public void Close_ActiveTab_ActivatesRightNeighbour()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        vm.Active = vm.Tabs[1];
        vm.Close(vm.Tabs[1]);
        Assert.Equal("https://c.pt", vm.Active!.Address);
    }

    [Fact]
    public void Close_ActiveLastTab_ActivatesLeftNeighbour()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt");
        vm.Close(vm.Tabs[1]);
        Assert.Equal("https://a.pt", vm.Active!.Address);
    }

    [Fact]
    public void Close_InactiveTab_KeepsActive()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        vm.Close(vm.Tabs[0]);
        Assert.Equal("https://c.pt", vm.Active!.Address);
    }

    [Fact]
    public void Close_OnlyTab_ReturnsTrueAndLeavesNoActive()
    {
        var vm = WithTabs("https://a.pt");
        Assert.True(vm.Close(vm.Tabs[0]));
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.Active);
    }

    [Fact]
    public void ReopenClosed_RestoresAddressAtOriginalPosition()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        vm.Close(vm.Tabs[1]);
        var reopened = vm.ReopenClosed();
        Assert.NotNull(reopened);
        Assert.Same(reopened, vm.Tabs[1]);
        Assert.Equal("https://b.pt", reopened.Address);
        Assert.Same(reopened, vm.Active);
    }

    [Fact]
    public void ReopenClosed_WithNothingClosed_ReturnsNull()
        => Assert.Null(WithTabs("https://a.pt").ReopenClosed());

    [Fact]
    public void SelectNext_WrapsToFirst()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt");
        vm.SelectNext();
        Assert.Same(vm.Tabs[0], vm.Active);
    }

    [Fact]
    public void SelectPrevious_WrapsToLast()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt");
        vm.Active = vm.Tabs[0];
        vm.SelectPrevious();
        Assert.Same(vm.Tabs[1], vm.Active);
    }

    [Fact]
    public void TogglePin_MovesTabToEndOfPinnedGroup()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        vm.TogglePin(vm.Tabs[2]);
        vm.TogglePin(vm.Tabs[2]);
        Assert.Equal(["https://c.pt", "https://b.pt", "https://a.pt"], vm.Tabs.Select(t => t.Address));
        Assert.True(vm.Tabs[0].IsPinned && vm.Tabs[1].IsPinned && !vm.Tabs[2].IsPinned);
    }

    [Fact]
    public void TogglePin_UnpinGoesRightAfterPinnedGroup()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        vm.TogglePin(vm.Tabs[0]);
        vm.TogglePin(vm.Tabs[1]);
        vm.TogglePin(vm.Tabs[0]);
        Assert.Equal(["https://b.pt", "https://a.pt", "https://c.pt"], vm.Tabs.Select(t => t.Address));
        Assert.True(vm.Tabs[0].IsPinned && !vm.Tabs[1].IsPinned);
    }

    [Fact]
    public void Move_CannotCrossPinnedBoundary()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        vm.TogglePin(vm.Tabs[0]);
        vm.Move(vm.Tabs[2], 0);
        Assert.Equal("https://a.pt", vm.Tabs[0].Address);
        vm.Move(vm.Tabs[0], 2);
        Assert.Equal("https://a.pt", vm.Tabs[0].Address);
    }

    [Fact]
    public void NewTab_NeverGoesBeforePinnedTabs()
    {
        var vm = WithTabs("https://a.pt");
        vm.TogglePin(vm.Tabs[0]);
        vm.NewTab("https://b.pt", activate: false);
        Assert.Equal("https://a.pt", vm.Tabs[0].Address);
    }

    [Fact]
    public void CloseOthers_KeepsTabAndPinned()
    {
        var vm = WithTabs("https://p.pt", "https://a.pt", "https://b.pt", "https://c.pt");
        vm.TogglePin(vm.Tabs[0]);
        vm.CloseOthers(vm.Tabs[2]);
        Assert.Equal(["https://p.pt", "https://b.pt"], vm.Tabs.Select(t => t.Address));
        Assert.Equal("https://b.pt", vm.Active!.Address);
    }

    [Fact]
    public void CloseToRight_ClosesOnlyFollowingTabs()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        vm.CloseToRight(vm.Tabs[0]);
        Assert.Single(vm.Tabs);
        Assert.Equal("https://a.pt", vm.Active!.Address);
    }

    [Fact]
    public void Move_ReordersAndKeepsActive()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt");
        var active = vm.Active;
        vm.Move(vm.Tabs[0], 2);
        Assert.Equal(["https://b.pt", "https://c.pt", "https://a.pt"], vm.Tabs.Select(t => t.Address));
        Assert.Same(active, vm.Active);
    }

    [Fact]
    public void Move_ClampsOutOfRangeIndex()
    {
        var vm = WithTabs("https://a.pt", "https://b.pt");
        vm.Move(vm.Tabs[0], 99);
        Assert.Equal("https://a.pt", vm.Tabs[1].Address);
        vm.Move(vm.Tabs[1], -5);
        Assert.Equal("https://a.pt", vm.Tabs[0].Address);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(235, 1)]
    [InlineData(700, 3)]
    [InlineData(5000, 3)]
    [InlineData(-40, 0)]
    public void IndexAt_MapsPointerToSlot(double x, int expected)
        => Assert.Equal(expected, TabLayout.IndexAt(x, tabWidth: 232, count: 4));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(8, 3)]
    [InlineData(9, 3)]
    public void SelectByNumber_FollowsChromeShortcuts(int number, int expectedIndex)
    {
        var vm = WithTabs("https://a.pt", "https://b.pt", "https://c.pt", "https://d.pt");
        vm.SelectByNumber(number);
        Assert.Same(vm.Tabs[expectedIndex], vm.Active);
    }
}
