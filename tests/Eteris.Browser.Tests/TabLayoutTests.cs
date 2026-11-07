using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabLayoutTests
{
    [Fact]
    public void TabWidth_FewTabs_UsesMaximum()
        => Assert.Equal(TabLayout.MaxTabWidth, TabLayout.TabWidth(available: 1200, count: 3));

    [Fact]
    public void TabWidth_ManyTabs_SharesAvailableSpace()
        => Assert.Equal(125, TabLayout.TabWidth(available: 1000, count: 8));

    [Fact]
    public void TabWidth_Crowded_NeverBelowMinimum()
        => Assert.Equal(TabLayout.MinTabWidth, TabLayout.TabWidth(available: 300, count: 50));

    // Ícone (16) + espaços + umas 10 letras + «x»: a largura mínima tem de deixar o título legível.
    [Fact]
    public void MinTabWidth_IsLegible()
        => Assert.InRange(TabLayout.MinTabWidth, 110, 160);

    [Fact]
    public void TabWidth_NoTabs_ReturnsMaximum()
        => Assert.Equal(TabLayout.MaxTabWidth, TabLayout.TabWidth(available: 500, count: 0));

    [Theory]
    [InlineData(1000, 8, false)]
    [InlineData(1000, 9, true)]
    [InlineData(1000, 0, false)]
    public void Overflows_WhenTabsAtMinimumDoNotFit(double available, int count, bool expected)
        => Assert.Equal(expected, TabLayout.Overflows(available, count));

    // Aba já à vista: o deslocamento não mexe (nada salta ao mudar de aba).
    [Fact]
    public void RevealOffset_VisibleTab_KeepsOffset()
        => Assert.Equal(200, TabLayout.RevealOffset(offset: 200, tabLeft: 300, tabWidth: 120, viewport: 600, extent: 2400));

    [Fact]
    public void RevealOffset_TabToTheLeft_AlignsItsLeftEdge()
        => Assert.Equal(120, TabLayout.RevealOffset(offset: 500, tabLeft: 120, tabWidth: 120, viewport: 600, extent: 2400));

    [Fact]
    public void RevealOffset_TabToTheRight_AlignsItsRightEdge()
        => Assert.Equal(720, TabLayout.RevealOffset(offset: 0, tabLeft: 1200, tabWidth: 120, viewport: 600, extent: 2400));

    [Fact]
    public void RevealOffset_LastTab_ClampsToTheEnd()
        => Assert.Equal(1800, TabLayout.RevealOffset(offset: 0, tabLeft: 2280, tabWidth: 120, viewport: 600, extent: 2400));

    [Fact]
    public void RevealOffset_NothingToScroll_IsZero()
        => Assert.Equal(0, TabLayout.RevealOffset(offset: 50, tabLeft: 100, tabWidth: 120, viewport: 600, extent: 400));

    [Theory]
    [InlineData(0, 600, 400, false, false)]     // tudo cabe: sem setas
    [InlineData(0, 600, 2400, false, true)]     // no início: só para a direita
    [InlineData(900, 600, 2400, true, true)]    // a meio: ambas
    [InlineData(1800, 600, 2400, true, false)]  // no fim: só para a esquerda
    public void Arrows_OnlyWhereThereAreMoreTabs(double offset, double viewport, double extent, bool left, bool right)
        => Assert.Equal((left, right), TabLayout.Arrows(offset, viewport, extent));

    [Theory]
    [InlineData(10, -1)]
    [InlineData(300, 0)]
    [InlineData(590, 1)]
    public void DragScrollDirection_NearTheEdges(double x, int expected)
        => Assert.Equal(expected, TabLayout.DragScrollDirection(x, viewport: 600));

    [Fact]
    public void Step_StaysWithinBounds()
    {
        Assert.Equal(0, TabLayout.Step(offset: 40, direction: -1, viewport: 600, extent: 2400));
        Assert.Equal(TabLayout.ScrollStep, TabLayout.Step(offset: 0, direction: 1, viewport: 600, extent: 2400));
        Assert.Equal(1800, TabLayout.Step(offset: 1750, direction: 1, viewport: 600, extent: 2400));
    }

    [Fact]
    public void IndexAt_ClampsToTheStrip()
    {
        Assert.Equal(0, TabLayout.IndexAt(-50, 120, 5));
        Assert.Equal(2, TabLayout.IndexAt(250, 120, 5));
        Assert.Equal(4, TabLayout.IndexAt(5000, 120, 5));
        Assert.Equal(0, TabLayout.IndexAt(100, 120, 0));
    }
}
