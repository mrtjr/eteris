using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TidyTabsTests
{
    private static TabsViewModel With(params string[] urls)
    {
        var vm = new TabsViewModel();
        foreach (var url in urls) vm.NewTab(url);
        return vm;
    }

    [Fact]
    public void CloseDuplicates_KeepsOnePerAddressPreferringActive()
    {
        var vm = With("https://a.pt/", "https://b.pt/", "https://a.pt", "https://a.pt/#x", "https://c.pt/");
        vm.Active = vm.Tabs[2];
        var closed = vm.CloseDuplicates();
        Assert.Equal(1, closed);
        Assert.Equal(["https://b.pt/", "https://a.pt", "https://a.pt/#x", "https://c.pt/"], vm.Tabs.Select(t => t.Address));
        Assert.Equal("https://a.pt", vm.Active!.Address);
    }

    [Fact]
    public void CloseDuplicates_NeverClosesPinnedTabs()
    {
        var vm = With("https://a.pt/", "https://a.pt/");
        vm.TogglePin(vm.Tabs[1]);
        vm.CloseDuplicates();
        Assert.Single(vm.Tabs);
        Assert.True(vm.Tabs[0].IsPinned);
    }

    [Fact]
    public void SortBySite_GroupsSameDomainKeepingPinnedFirst()
    {
        var vm = With("https://b.pt/1", "https://a.pt/1", "https://www.b.pt/2", "https://a.pt/2", "https://c.pt/");
        vm.TogglePin(vm.Tabs[4]);
        vm.SortBySite();
        Assert.Equal(["https://c.pt/", "https://a.pt/1", "https://a.pt/2", "https://b.pt/1", "https://www.b.pt/2"],
            vm.Tabs.Select(t => t.Address));
    }

    [Fact]
    public void AppendSleeping_KeepsOrderAndActiveTab()
    {
        var vm = With("https://a.pt/");
        vm.AppendSleeping("https://b.pt/", "B");
        vm.AppendSleeping("https://c.pt/");
        Assert.Equal(["https://a.pt/", "https://b.pt/", "https://c.pt/"], vm.Tabs.Select(t => t.Address));
        Assert.Equal("https://a.pt/", vm.Active!.Address);
        Assert.True(vm.Tabs[1].IsSleeping);
        Assert.Equal("B", vm.Tabs[1].Title);
    }

    [Fact]
    public void SortBySite_LeavesGroupedTabsAlone()
    {
        var vm = With("https://z.pt/", "https://b.pt/", "https://a.pt/");
        var group = vm.NewGroup(vm.Tabs[0]);
        vm.SortBySite();
        Assert.Same(group, vm.Tabs[0].Group);
        Assert.Equal(["https://z.pt/", "https://a.pt/", "https://b.pt/"], vm.Tabs.Select(t => t.Address));
    }
}
