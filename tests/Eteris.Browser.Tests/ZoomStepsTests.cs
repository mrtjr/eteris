using Eteris.Browser.Palette;

namespace Eteris.Browser.Tests;

public class ZoomStepsTests
{
    [Theory]
    [InlineData(1.0, 1, 1.1)]
    [InlineData(1.0, -1, 0.9)]
    [InlineData(1.1, 1, 1.25)]
    [InlineData(1.17, 1, 1.25)]
    [InlineData(1.17, -1, 1.1)]
    [InlineData(5.0, 1, 5.0)]
    [InlineData(0.25, -1, 0.25)]
    public void Next_FollowsChromeZoomLadder(double current, int direction, double expected)
        => Assert.Equal(expected, ZoomSteps.Next(current, direction), 3);

    [Fact]
    public void LevelAndFactor_RoundTrip()
    {
        Assert.Equal(0, ZoomSteps.ToLevel(1.0), 6);
        Assert.Equal(1.25, ZoomSteps.ToFactor(ZoomSteps.ToLevel(1.25)), 6);
    }

    [Fact]
    public void Percent_IsRounded()
        => Assert.Equal(110, ZoomSteps.Percent(ZoomSteps.ToLevel(1.1)));
}
