using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// O trabalho do arranque que a 1.ª página não precisa espera por ela (Infrastructure/FirstPaint.cs).
public class FirstPaintTests
{
    private readonly List<Action> _scheduled = [];
    private FirstPaint New() => new(_scheduled.Add);

    [Fact]
    public void Then_WaitsForTheSignalAndKeepsTheOrder()
    {
        var paint = New();
        var ran = new List<int>();
        paint.Then(() => ran.Add(1));
        paint.Then(() => ran.Add(2));
        Assert.Empty(_scheduled);
        Assert.False(paint.HasHappened);
        paint.Signal();
        Assert.True(paint.HasHappened);
        foreach (var action in _scheduled) action();
        Assert.Equal([1, 2], ran);
    }

    [Fact]
    public void Then_AfterTheSignalIsScheduledAtOnce()
    {
        var paint = New();
        paint.Signal();
        paint.Then(() => { });
        Assert.Single(_scheduled);
    }

    [Fact]
    public void Signal_RunsEachTaskOnlyOnce()
    {
        var paint = New();
        paint.Then(() => { });
        paint.Signal();
        paint.Signal();
        Assert.Single(_scheduled);
    }

    [Fact]
    public async Task SignalAfter_IsTheSafetyNet()
    {
        var paint = New();
        paint.Then(() => { });
        paint.SignalAfter(TimeSpan.FromMilliseconds(20));
        for (var i = 0; i < 200 && !paint.HasHappened; i++) await Task.Delay(10);
        Assert.True(paint.HasHappened);
        Assert.Single(_scheduled);
    }
}
