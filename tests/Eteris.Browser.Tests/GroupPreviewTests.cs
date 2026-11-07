using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class GroupPreviewTests
{
    private static (TabsViewModel Vm, TabGroup Group) Grouped(int members, int others = 1)
    {
        var vm = new TabsViewModel();
        for (var i = 0; i < members + others; i++) vm.NewTab($"https://s{i}.pt/pagina");
        var group = vm.NewGroup(vm.Tabs[0]);
        for (var i = 1; i < members; i++) vm.AddToGroup(vm.Tabs[i], group);
        return (vm, group);
    }

    [Fact]
    public void Tiles_OnlyMembersInStripOrder_WithActiveMarked()
    {
        var (vm, group) = Grouped(3, others: 2);
        vm.Tabs[1].Title = "Segunda";
        vm.Active = vm.Tabs[1];
        var tiles = GroupPreview.Tiles(group, vm.Tabs, vm.Active);
        Assert.Equal(3, tiles.Count);
        Assert.Equal(["s0.pt", "s1.pt", "s2.pt"], tiles.Select(t => t.Host));
        Assert.Equal([false, true, false], tiles.Select(t => t.IsActive));
        Assert.Equal("Segunda", tiles[1].Title);
    }

    [Fact]
    public void Tiles_FallBackToHostWhenTitleIsBlank()
    {
        var (vm, group) = Grouped(1);
        vm.Tabs[0].Title = "  ";
        Assert.Equal("s0.pt", GroupPreview.Tiles(group, vm.Tabs, null).Single().Title);
    }

    [Fact]
    public void Tiles_AreCappedAndSummarySaysSo()
    {
        var (vm, group) = Grouped(GroupPreview.MaxTiles + 2, others: 0);
        Assert.Equal(GroupPreview.MaxTiles, GroupPreview.Tiles(group, vm.Tabs, null).Count);
        Assert.Contains($"a mostrar {GroupPreview.MaxTiles}", GroupPreview.Summary(group, vm.Tabs));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(9, 3)]
    public void Columns_GrowUpToThree(int count, int expected) => Assert.Equal(expected, GroupPreview.Columns(count));

    [Fact]
    public void CardWidth_FitsTheColumns()
    {
        Assert.Equal(GroupPreview.TileWidth + 2 * GroupPreview.TilePadding, GroupPreview.CardWidth(1));
        Assert.Equal(3 * (GroupPreview.TileWidth + GroupPreview.TilePadding) + GroupPreview.TilePadding, GroupPreview.CardWidth(7));
    }

    [Fact]
    public void Left_CentersUnderThePill()
        => Assert.Equal(400 + 45 - 150, GroupPreview.Left(pillLeft: 400, pillWidth: 90, cardWidth: 300, areaWidth: 1200));

    [Fact]
    public void Left_StaysInsideTheArea()
    {
        Assert.Equal(8, GroupPreview.Left(pillLeft: 10, pillWidth: 90, cardWidth: 300, areaWidth: 1200));
        Assert.Equal(1200 - 300 - 8, GroupPreview.Left(pillLeft: 1150, pillWidth: 90, cardWidth: 300, areaWidth: 1200));
        // Janela mais estreita do que o cartão: encosta à esquerda em vez de ir para valores negativos.
        Assert.Equal(8, GroupPreview.Left(pillLeft: 100, pillWidth: 90, cardWidth: 500, areaWidth: 400));
    }

    [Fact]
    public void Summary_CountsMembersCollapseAndSleep()
    {
        var (vm, group) = Grouped(3);
        Assert.Equal("3 abas", GroupPreview.Summary(group, vm.Tabs));
        vm.Tabs[1].IsSleeping = true;
        vm.Tabs[2].IsSleeping = true;
        group.IsCollapsed = true;
        Assert.Equal("3 abas · encolhido · 2 a dormir", GroupPreview.Summary(group, vm.Tabs));
        var (single, one) = Grouped(1);
        Assert.Equal("1 aba", GroupPreview.Summary(one, single.Tabs));
    }

    [Fact]
    public void ShouldStay_WhileOverPillOrCard()
    {
        Assert.True(GroupPreview.ShouldStay(overPill: true, overCard: false));
        Assert.True(GroupPreview.ShouldStay(overPill: false, overCard: true));
        Assert.False(GroupPreview.ShouldStay(overPill: false, overCard: false));
    }
}
