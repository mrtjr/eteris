using System.Windows;

namespace Eteris.Browser.Tests;

public class MiniWindowTests
{
    [Fact]
    public void MiniBounds_SitsInTheBottomRightCornerOfTheWorkArea()
    {
        var bounds = MainWindow.MiniBounds(new Rect(0, 0, 1920, 1040));
        Assert.Equal(new Rect(1920 - 480 - 16, 1040 - 290 - 16, 480, 290), bounds);
    }

    [Fact]
    public void MiniBounds_FollowsAWorkAreaOnASecondScreen()
    {
        var bounds = MainWindow.MiniBounds(new Rect(-1280, 100, 1280, 900), 400, 240, 10);
        Assert.Equal(-10 - 400, bounds.Left);
        Assert.Equal(100 + 900 - 240 - 10, bounds.Top);
    }
}
