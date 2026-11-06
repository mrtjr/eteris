using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class SleepPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void InactiveForLongerThanThreshold_Sleeps()
        => Assert.True(SleepPolicy.ShouldSleep(isActive: false, isSleeping: false, isLoading: false, isAudible: false,
            lastActiveUtc: Now.AddMinutes(-21), Now));

    [Fact]
    public void RecentlyUsed_StaysAwake()
        => Assert.False(SleepPolicy.ShouldSleep(false, false, false, false, Now.AddMinutes(-5), Now));

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void ActiveSleepingLoadingOrPlayingAudio_NeverSleeps(bool active, bool sleeping, bool loading, bool audible)
        => Assert.False(SleepPolicy.ShouldSleep(active, sleeping, loading, audible, Now.AddHours(-3), Now));

    [Fact]
    public void Restore_KeepsOrderActivatesChosenAndSleepsTheRest()
    {
        var vm = new TabsViewModel();
        vm.Restore(["https://a.pt/", "https://b.pt/", "https://c.pt/"], activeIndex: 1);
        Assert.Equal(["https://a.pt/", "https://b.pt/", "https://c.pt/"], vm.Tabs.Select(t => t.Address));
        Assert.Same(vm.Tabs[1], vm.Active);
        Assert.False(vm.Tabs[1].IsSleeping);
        Assert.True(vm.Tabs[0].IsSleeping);
        Assert.True(vm.Tabs[2].IsSleeping);
    }
}
