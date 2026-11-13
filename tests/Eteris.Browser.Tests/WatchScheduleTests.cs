using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// A agenda da vigia: nunca menos de 30 min entre visitas, ±10 % de variação para as visitas não caírem sempre à mesma
// hora, recuo a dobrar em cada falha (no máximo um dia) e nenhuma visita às vigias bloqueadas ou sem pedaço.
public class WatchScheduleTests
{
    private static readonly DateTime Now = new(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Constants() => Assert.Equal((30, 120), (WatchSchedule.MinIntervalMinutes, WatchSchedule.DefaultIntervalMinutes));

    [Fact] public void Next_NeverBelowThirtyMinutes()
        => Assert.True(WatchSchedule.Next(Now, 5, 0, WatchStatus.Ok, new Random(1)) >= Now.AddMinutes(30 * 0.9));

    [Fact] public void Next_JittersWithinTenPercent()
    {
        var next = WatchSchedule.Next(Now, 120, 0, WatchStatus.Ok, new Random(7));
        Assert.InRange((next - Now).TotalMinutes, 108, 132);
    }

    [Fact] public void Next_JitterActuallyVaries()
    {
        var rng = new Random(3);
        var minutes = Enumerable.Range(0, 20).Select(_ => (WatchSchedule.Next(Now, 120, 0, WatchStatus.Ok, rng) - Now).TotalMinutes).ToList();
        Assert.True(minutes.Distinct().Count() > 1);
        Assert.All(minutes, m => Assert.InRange(m, 108, 132));
    }

    [Fact] public void Next_BacksOffOnFailures_UpToADay()
    {
        Assert.InRange((WatchSchedule.Next(Now, 120, 2, WatchStatus.Failing, new Random(1)) - Now).TotalMinutes, 432, 528);
        Assert.True(WatchSchedule.Next(Now, 120, 10, WatchStatus.Failing, new Random(1)) <= Now.AddHours(24));
    }

    [Fact] public void Next_HugeFailureCount_StaysWithinADay()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var next = WatchSchedule.Next(Now, 1440, int.MaxValue, WatchStatus.Failing, new Random(seed));
            Assert.InRange(next, Now.AddHours(20), Now.AddHours(24));
        }
    }

    [Fact] public void Next_BlockedAndMissing_AreNotVisitedAgain()
    {
        Assert.Equal(DateTime.MaxValue, WatchSchedule.Next(Now, 120, 2, WatchStatus.Blocked, new Random(1)));
        Assert.Equal(DateTime.MaxValue, WatchSchedule.Next(Now, 120, 2, WatchStatus.Missing, new Random(1)));
    }
}
