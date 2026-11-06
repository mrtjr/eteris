using System.Windows.Shell;
using Eteris.Browser.Downloads;

namespace Eteris.Browser.Tests;

public class TaskbarProgressTests
{
    [Theory]
    [InlineData(false, 0, TaskbarItemProgressState.None, 0)]
    [InlineData(true, 0, TaskbarItemProgressState.Indeterminate, 0)]
    [InlineData(true, 40, TaskbarItemProgressState.Normal, 0.4)]
    public void State_FollowsDownloads(bool active, int percent, TaskbarItemProgressState state, double value)
        => Assert.Equal((state, value), TaskbarProgress.For(active, percent));

    [Fact]
    public void FinishingWhileAway_ShouldFlash()
    {
        Assert.True(TaskbarProgress.ShouldFlash(wasActive: true, isActive: false, windowFocused: false));
        Assert.False(TaskbarProgress.ShouldFlash(wasActive: true, isActive: false, windowFocused: true));
        Assert.False(TaskbarProgress.ShouldFlash(wasActive: false, isActive: false, windowFocused: false));
    }
}
