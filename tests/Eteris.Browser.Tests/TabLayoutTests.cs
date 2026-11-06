using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabLayoutTests
{
    [Fact]
    public void TabWidth_FewTabs_UsesMaximum()
        => Assert.Equal(TabLayout.MaxTabWidth, TabLayout.TabWidth(available: 1200, count: 3));

    [Fact]
    public void TabWidth_ManyTabs_SharesAvailableSpace()
        => Assert.Equal(100, TabLayout.TabWidth(available: 1000, count: 10));

    [Fact]
    public void TabWidth_Crowded_NeverBelowMinimum()
        => Assert.Equal(TabLayout.MinTabWidth, TabLayout.TabWidth(available: 300, count: 50));

    [Fact]
    public void TabWidth_NoTabs_ReturnsMaximum()
        => Assert.Equal(TabLayout.MaxTabWidth, TabLayout.TabWidth(available: 500, count: 0));
}
