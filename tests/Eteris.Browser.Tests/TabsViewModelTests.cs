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
