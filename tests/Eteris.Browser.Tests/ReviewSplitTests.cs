using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Correções da revisão do bloco A no ecrã dividido e no arrasto de abas.
public class ReviewSplitTests
{
    private static TabsViewModel WithTabs(params string[] urls)
    {
        var vm = new TabsViewModel();
        foreach (var url in urls) vm.NewTab(url);
        return vm;
    }

    [Fact]
    public void ClosingTheActiveHalf_LeavesThePartner_NotTheNeighbourInTheStrip()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/", "https://c.pt/");
        var (a, c) = (vm.Tabs[0], vm.Tabs[2]);
        var split = new SplitView(a, c, SplitOrientation.SideBySide);
        vm.PreferredSuccessor = tab => split.PartnerOf(tab);
        vm.Active = a;
        vm.Close(a);
        Assert.Same(c, vm.Active);
    }

    [Fact]
    public void ClosingWithoutPreference_OrWithAPartnerGone_FallsBackToTheNeighbour()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/", "https://c.pt/");
        vm.Active = vm.Tabs[0];
        vm.Close(vm.Tabs[0]);
        Assert.Equal("https://b.pt/", vm.Active!.Address);

        var ghost = new BrowserTab("https://fora.pt/");
        vm.PreferredSuccessor = _ => ghost;
        vm.Active = vm.Tabs[0];
        vm.Close(vm.Tabs[0]);
        Assert.Equal("https://c.pt/", vm.Active!.Address);
    }

    [Fact]
    public void MovingTheActiveHalfToAnotherSpace_LeavesThePartner()
    {
        var vm = WithTabs("https://a.pt/", "https://b.pt/", "https://c.pt/");
        var work = vm.NewWorkspace("Trabalho", activate: false);
        var (a, c) = (vm.Tabs[0], vm.Tabs[2]);
        var split = new SplitView(a, c, SplitOrientation.SideBySide);
        vm.PreferredSuccessor = tab => split.PartnerOf(tab);
        vm.Active = a;
        Assert.True(vm.MoveToWorkspace(a, work));
        Assert.Same(c, vm.Active);
    }

    [Fact]
    public void PinnedTabMovedIntoASleepingSpace_KeepsItsSplitAndActiveTab()
    {
        var vm = WithTabs("https://x.pt/", "https://y.pt/", "https://z.pt/");
        var general = vm.Current;
        vm.Active = vm.Tabs[1];
        var work = vm.NewWorkspace("Trabalho");
        Assert.False(general.IsCurrent);
        general.StoredSplit = new SessionSplit(1, 2, "side");

        var pinned = vm.NewTab("https://p.pt/");
        vm.TogglePin(pinned);
        Assert.True(vm.MoveToWorkspace(pinned, general));
        Assert.Equal(["https://p.pt/", "https://x.pt/", "https://y.pt/", "https://z.pt/"], vm.TabsOf(general).Select(t => t.Address));
        // Continua a ser Y|Z, não X|Y.
        Assert.Equal(2, general.StoredSplit!.Left);
        Assert.Equal(3, general.StoredSplit.Right);

        Assert.True(vm.SwitchWorkspace(general));
        Assert.Equal("https://y.pt/", vm.Active!.Address);
    }

    [Fact]
    public void UnpinnedTabMovedToTheEnd_ChangesNoIndex()
    {
        var vm = WithTabs("https://x.pt/", "https://y.pt/");
        var general = vm.Current;
        vm.NewWorkspace("Trabalho");
        general.StoredSplit = new SessionSplit(0, 1, "side");
        var tab = vm.NewTab("https://n.pt/");
        Assert.True(vm.MoveToWorkspace(tab, general));
        Assert.Equal(0, general.StoredSplit!.Left);
        Assert.Equal(1, general.StoredSplit.Right);
    }

    [Theory]
    [InlineData(1006, 300, 16, 0, SplitDropTarget.Right)]   // na moldura, rato encostado ao bordo
    [InlineData(1016, 300, 16, 0, SplitDropTarget.Right)]
    [InlineData(1017, 300, 16, 0, SplitDropTarget.None)]    // para lá da janela
    [InlineData(500, 806, 0, 6, SplitDropTarget.Bottom)]
    [InlineData(1006, 300, 0, 0, SplitDropTarget.None)]     // sem folga, como antes
    public void TargetAt_ToleratesTheFrameUpToTheWindowEdge(double x, double y, double slackRight, double slackBottom, SplitDropTarget expected)
        => Assert.Equal(expected, SplitDropZone.TargetAt(x, y, 1000, 800, slackRight, slackBottom));

    [Theory]
    [InlineData(20, 40, true)]
    [InlineData(-10, 40, true)]
    [InlineData(50, 40, true)]
    [InlineData(60, 40, false)]   // já sobre a página
    [InlineData(-20, 40, false)]
    public void DraggedTab_ReordersOnlyOverTheStrip(double y, double height, bool expected)
        => Assert.Equal(expected, TabLayout.ReordersAt(y, height));

    // Arranque com um endereço (run-test.ps1 -Url, atalho, «Abrir com»): fica exatamente uma aba com ele, seja a
    // janela dona da sessão (o plano já o abre) ou outra janela (sem plano).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartingWithAnAddress_OpensItExactlyOnce(bool ownsSession)
    {
        const string initialUrl = "https://pt.wikipedia.org/";
        var vm = new TabsViewModel();
        if (ownsSession)
            foreach (var tab in Eteris.Browser.Browser.StartupPages.Plan(false, "1.0.0", "1.0.0", initialUrl, hasRestoredTabs: false))
                vm.NewTab(tab.Url, tab.Activate);
        if (Eteris.Browser.Browser.StartupPages.OpensInitialUrlSeparately(ownsSession, initialUrl)) vm.NewTab(initialUrl);
        Assert.Single(vm.Tabs, t => t.Address == initialUrl);
        Assert.Equal(initialUrl, vm.Active!.Address);
    }

    [Fact]
    public void StartingWithoutAnAddress_NeverOpensAnExtraTab()
    {
        Assert.False(Eteris.Browser.Browser.StartupPages.OpensInitialUrlSeparately(true, null));
        Assert.False(Eteris.Browser.Browser.StartupPages.OpensInitialUrlSeparately(false, null));
    }
}
