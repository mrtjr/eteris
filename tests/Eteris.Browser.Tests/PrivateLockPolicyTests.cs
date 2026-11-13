using System.Windows.Input;
using Eteris.Browser.Tabs;
using Microsoft.Win32;

namespace Eteris.Browser.Tests;

// A tranca das janelas privadas (Tabs/PrivateLockPolicy.cs, MainWindow.PrivateLock.cs): quando tranca, quando a
// inatividade conta e qual é a tecla. Como no Safari: o bloqueio do Windows, a suspensão e a janela parada; uma janela só
// com abas novas não se tranca sozinha. Nunca por falta de uso com som, vídeo ou uma chamada.
public class PrivateLockPolicyTests
{
    private static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void LocksOn_WindowsLockDisconnectLogoffAndSuspend()
    {
        foreach (var reason in new[] { SessionSwitchReason.SessionLock, SessionSwitchReason.ConsoleDisconnect, SessionSwitchReason.RemoteDisconnect, SessionSwitchReason.SessionLogoff })
            Assert.True(PrivateLockPolicy.LocksOn(reason), reason.ToString());
        foreach (var reason in new[] { SessionSwitchReason.SessionUnlock, SessionSwitchReason.ConsoleConnect, SessionSwitchReason.RemoteConnect, SessionSwitchReason.SessionLogon, SessionSwitchReason.SessionRemoteControl })
            Assert.False(PrivateLockPolicy.LocksOn(reason), reason.ToString());
        Assert.True(PrivateLockPolicy.LocksOn(PowerModes.Suspend));
        Assert.False(PrivateLockPolicy.LocksOn(PowerModes.Resume));
        Assert.False(PrivateLockPolicy.LocksOn(PowerModes.StatusChange));
    }

    [Fact]
    public void Manual_LocksEvenWithoutContentOrTheSetting()
    {
        Assert.True(PrivateLockPolicy.ShouldLock(PrivateLockReason.Manual, enabled: false, hasContent: false, alreadyLocked: false));
        Assert.False(PrivateLockPolicy.ShouldLock(PrivateLockReason.Manual, enabled: true, hasContent: true, alreadyLocked: true));
    }

    [Theory]
    [InlineData(PrivateLockReason.WindowsLocked)]
    [InlineData(PrivateLockReason.Suspended)]
    [InlineData(PrivateLockReason.Idle)]
    public void Automatic_NeedsTheSettingAndAPage(PrivateLockReason reason)
    {
        Assert.True(PrivateLockPolicy.ShouldLock(reason, enabled: true, hasContent: true, alreadyLocked: false));
        Assert.False(PrivateLockPolicy.ShouldLock(reason, enabled: false, hasContent: true, alreadyLocked: false));
        Assert.False(PrivateLockPolicy.ShouldLock(reason, enabled: true, hasContent: false, alreadyLocked: false));
        Assert.False(PrivateLockPolicy.ShouldLock(reason, enabled: true, hasContent: true, alreadyLocked: true));
    }

    [Fact]
    public void LastUse_FollowsTheInputOnlyWhileActive()
    {
        Assert.Equal(Min(98), PrivateLockPolicy.LastUse(Min(90), active: true, now: Min(100), lastInputAgo: Min(2)));
        Assert.Equal(Min(90), PrivateLockPolicy.LastUse(Min(90), active: false, now: Min(100), lastInputAgo: Min(2)));
        // Nunca anda para trás: a entrada do sistema mais antiga do que o último uso já contado não o recua.
        Assert.Equal(Min(99), PrivateLockPolicy.LastUse(Min(99), active: true, now: Min(100), lastInputAgo: Min(5)));
    }

    // GetLastInputInfo dá um tique de 32 bits: a conta aguenta a volta dos 49,7 dias.
    [Fact]
    public void InputAgo_SurvivesTheTickWrap()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2000), PrivateLockPolicy.InputAgo(tickNow: 5000, lastInputTick: 3000));
        Assert.Equal(TimeSpan.FromMilliseconds(1100), PrivateLockPolicy.InputAgo(tickNow: 100, lastInputTick: uint.MaxValue - 999));
    }

    [Fact]
    public void IdleLock_NeverWithSoundVideoOrACall()
    {
        Assert.False(PrivateLockPolicy.IdleLockDue(15, Min(15) - TimeSpan.FromSeconds(1), playing: false, capturing: false));
        Assert.True(PrivateLockPolicy.IdleLockDue(15, Min(15), playing: false, capturing: false));
        Assert.False(PrivateLockPolicy.IdleLockDue(15, Min(15), playing: true, capturing: false));
        Assert.False(PrivateLockPolicy.IdleLockDue(15, Min(15), playing: false, capturing: true));
        Assert.False(PrivateLockPolicy.IdleLockDue(0, TimeSpan.FromHours(10), playing: false, capturing: false));
    }

    // O gancho de teste ETERIS_TEST_TRANCA_PARADA troca os minutos por segundos (só com a inatividade ligada).
    [Fact]
    public void IdleLimit_IsTheMinutesOrTheTestSeconds()
    {
        Assert.Equal(Min(15), PrivateLockPolicy.IdleLimit(15, testSeconds: null));
        Assert.Equal(TimeSpan.FromSeconds(20), PrivateLockPolicy.IdleLimit(15, testSeconds: 20));
        Assert.Null(PrivateLockPolicy.IdleLimit(0, testSeconds: 20));
        Assert.Null(PrivateLockPolicy.IdleLimit(-5, testSeconds: null));
        Assert.True(PrivateLockPolicy.IdleLockDue(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), playing: false, capturing: false));
        Assert.False(PrivateLockPolicy.IdleLockDue(null, TimeSpan.FromHours(1), playing: false, capturing: false));
    }

    [Fact]
    public void Choices_AreOff5_15_60_AndTheDefaultIs15()
    {
        Assert.Equal([0, 5, 15, 60], PrivateLockPolicy.Choices);
        Assert.Equal(15, PrivateLockPolicy.DefaultMinutes);
        Assert.True(PrivateLockPolicy.IsChoice(0));
        Assert.True(PrivateLockPolicy.IsChoice(60));
        Assert.False(PrivateLockPolicy.IsChoice(30));
        Assert.False(PrivateLockPolicy.IsChoice(-5));
    }

    [Fact]
    public void LockKey_IsCtrlAltL_WithTheLeftAltOnly()
    {
        Assert.True(PrivateLockPolicy.IsLockKey(Key.L, shift: false, leftAlt: true));
        Assert.False(PrivateLockPolicy.IsLockKey(Key.L, shift: false, leftAlt: false));
        Assert.False(PrivateLockPolicy.IsLockKey(Key.L, shift: true, leftAlt: true));
        Assert.False(PrivateLockPolicy.IsLockKey(Key.K, shift: false, leftAlt: true));
    }
}
