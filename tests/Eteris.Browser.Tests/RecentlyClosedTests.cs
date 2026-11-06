using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class RecentlyClosedTests
{
    private static TabsViewModel With(params string[] urls)
    {
        var vm = new TabsViewModel();
        foreach (var url in urls) vm.NewTab(url);
        return vm;
    }

    [Fact]
    public void RecentlyClosed_IsNewestFirstWithTitles()
    {
        var vm = With("https://a.pt/", "https://b.pt/", "https://c.pt/");
        vm.Tabs[0].Title = "A";
        vm.Tabs[1].Title = "B";
        vm.Close(vm.Tabs[0]);
        vm.Close(vm.Tabs[0]);
        Assert.Equal(["B", "A"], vm.RecentlyClosed.Select(c => c.Title));
    }

    [Fact]
    public void ReopenSpecificEntry_RemovesOnlyIt()
    {
        var vm = With("https://a.pt/", "https://b.pt/", "https://c.pt/");
        vm.Close(vm.Tabs[0]);
        vm.Close(vm.Tabs[0]);
        var older = vm.RecentlyClosed[1];
        var tab = vm.ReopenClosed(older)!;
        Assert.Equal("https://a.pt/", tab.Address);
        Assert.Same(tab, vm.Active);
        Assert.Equal(["https://b.pt/"], vm.RecentlyClosed.Select(c => c.Address));
    }

    [Fact]
    public void NewTabPages_AreNotRemembered()
    {
        var vm = With("https://a.pt/", "eteris://newtab/");
        vm.Close(vm.Tabs[1]);
        Assert.Empty(vm.RecentlyClosed);
    }

    [Fact]
    public void ForgetClosed_EmptiesTheList()
    {
        var vm = With("https://a.pt/", "https://b.pt/");
        vm.Close(vm.Tabs[0]);
        vm.ForgetClosed();
        Assert.Empty(vm.RecentlyClosed);
        Assert.Null(vm.ReopenClosed());
    }

    [Fact]
    public void List_IsCapped()
    {
        var vm = With(Enumerable.Range(0, 40).Select(i => $"https://s{i}.pt/").Append("https://keep.pt/").ToArray());
        for (var i = 0; i < 40; i++) vm.Close(vm.Tabs[0]);
        Assert.Equal(TabsViewModel.MaxClosed, vm.RecentlyClosed.Count);
    }
}
