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

    // DAD-03: câmara, microfone ou ecrã em uso, multimédia a tocar (mesmo sem som) e a página que pediu para ficar.
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CapturingPlayingOrHeld_NeverSleeps(bool capturing, bool playing, bool held)
        => Assert.False(SleepPolicy.ShouldSleep(false, false, false, false, Now.AddHours(-3), Now,
            isCapturing: capturing, isPlaying: playing, isHeld: held));

    // DES-11: o tempo em que o computador esteve suspenso não conta para o prazo.
    [Fact]
    public void SuspendGap_IsNotCounted()
    {
        Assert.True(SleepPolicy.WasSuspended(Now.AddMinutes(-30), Now));
        Assert.False(SleepPolicy.WasSuspended(Now.AddMinutes(-1), Now));
        Assert.False(SleepPolicy.WasSuspended(null, Now));
        // Usada 1 min antes de o PC suspender 30 min: no minuto a seguir ao regresso continua acordada.
        var lastActive = SleepPolicy.Later(Now.AddMinutes(-31), floorUtc: Now);
        Assert.False(SleepPolicy.ShouldSleep(false, false, false, false, lastActive, Now.AddMinutes(1)));
        Assert.Equal(Now.AddMinutes(-5), SleepPolicy.Later(Now.AddMinutes(-5), floorUtc: null));
    }

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
