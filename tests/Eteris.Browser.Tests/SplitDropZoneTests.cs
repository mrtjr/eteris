using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class SplitDropZoneTests
{
    [Theory]
    [InlineData(990, 300, SplitDropTarget.Right)]
    [InlineData(976, 10, SplitDropTarget.Right)]
    [InlineData(500, 790, SplitDropTarget.Bottom)]
    [InlineData(990, 790, SplitDropTarget.Right)]
    [InlineData(500, 300, SplitDropTarget.None)]
    [InlineData(975, 300, SplitDropTarget.None)]
    [InlineData(1001, 300, SplitDropTarget.None)]
    [InlineData(990, -5, SplitDropTarget.None)]
    [InlineData(500, 801, SplitDropTarget.None)]
    public void TargetAt_IsTheRightOrBottomBand(double x, double y, SplitDropTarget expected)
        => Assert.Equal(expected, SplitDropZone.TargetAt(x, y, 1000, 800));

    [Fact]
    public void TargetAt_EmptyArea_IsNone()
        => Assert.Equal(SplitDropTarget.None, SplitDropZone.TargetAt(0, 0, 0, 0));

    [Fact]
    public void Orientation_FollowsTheEdge()
    {
        Assert.Equal(SplitOrientation.SideBySide, SplitDropZone.OrientationOf(SplitDropTarget.Right));
        Assert.Equal(SplitOrientation.Stacked, SplitDropZone.OrientationOf(SplitDropTarget.Bottom));
    }

    [Fact]
    public void CanSplit_NeedsTwoDifferentTabs()
    {
        var a = new BrowserTab("https://a.pt/");
        var b = new BrowserTab("https://b.pt/");
        Assert.True(SplitDropZone.CanSplit(a, b));
        Assert.False(SplitDropZone.CanSplit(a, a));
        Assert.False(SplitDropZone.CanSplit(null, b));
        Assert.False(SplitDropZone.CanSplit(a, null));
    }
}
