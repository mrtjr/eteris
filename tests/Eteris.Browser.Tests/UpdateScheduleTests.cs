using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Updates;

namespace Eteris.Browser.Tests;

// INF-07 (+CAD-06, PRO-08, EGR-02): a procura de atualizações não é só a do arranque. Repete-se de 6 em 6 horas (com
// variação), mais cedo depois de uma falha, quando a rede volta ou o computador retoma; uma versão pronta não impede
// de saber de uma mais nova.
public class UpdateScheduleTests
{
    [Fact]
    public void Next_AfterFailures_Is5Then15Then60MinutesThenRegular()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), UpdateSchedule.Next(UpdateState.Failed, 1, 0));
        Assert.Equal(TimeSpan.FromMinutes(15), UpdateSchedule.Next(UpdateState.Failed, 2, 0));
        Assert.Equal(TimeSpan.FromMinutes(60), UpdateSchedule.Next(UpdateState.Failed, 3, 0));
        Assert.Equal(TimeSpan.FromHours(6), UpdateSchedule.Next(UpdateState.Failed, 4, 0));
        Assert.Equal(TimeSpan.FromHours(6), UpdateSchedule.Next(UpdateState.Failed, 400, 0));
    }

    [Fact]
    public void Next_AfterUpToDateOrReady_IsSixHoursPlusJitter()
    {
        Assert.Equal(TimeSpan.FromHours(6), UpdateSchedule.Next(UpdateState.UpToDate, 0, 0));
        Assert.Equal(TimeSpan.FromMinutes(375), UpdateSchedule.Next(UpdateState.UpToDate, 0, 0.5));
        Assert.Equal(TimeSpan.FromHours(6), UpdateSchedule.Next(UpdateState.Ready, 0, 0));
        Assert.Equal(TimeSpan.FromMinutes(390), UpdateSchedule.Next(UpdateState.Failed, 9, 1));
    }

    [Fact]
    public void Next_InDevelopment_NeverChecksAgain() => Assert.Null(UpdateSchedule.Next(UpdateState.Development, 0, 0));

    [Theory]
    [InlineData(UpdateState.Failed, 1, true)] [InlineData(UpdateState.UpToDate, 59, false)] [InlineData(UpdateState.UpToDate, 60, true)]
    [InlineData(UpdateState.Ready, 60, true)] [InlineData(UpdateState.Development, 600, false)]
    public void CheckOnWake_AfterAFailureOrAnHour(UpdateState state, int minutes, bool expected)
        => Assert.Equal(expected, UpdateSchedule.CheckOnWake(state, TimeSpan.FromMinutes(minutes)));

    [Fact]
    public async Task Scheduler_RetriesAfterFailuresAndStopsInDevelopment()
    {
        UpdateState[] results = [UpdateState.Failed, UpdateState.Failed, UpdateState.Failed, UpdateState.UpToDate, UpdateState.Development];
        var state = UpdateState.Checking;
        var checks = 0;
        var delays = new List<TimeSpan>();
        var scheduler = new UpdateScheduler(
            () => { state = results[checks++]; return Task.CompletedTask; },
            () => state,
            (wait, _) => { delays.Add(wait); return Task.CompletedTask; },
            () => 0,
            () => DateTime.UtcNow);

        await scheduler.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60), TimeSpan.FromHours(6)], delays);
        Assert.Equal(5, checks);
    }

    [Fact]
    public async Task Scheduler_ACheckThatThrows_IsAFailureNotTheEndOfTheLoop()
    {
        var checks = 0;
        var state = UpdateState.Checking;
        var delays = new List<TimeSpan>();
        var scheduler = new UpdateScheduler(
            () => { checks++; if (checks == 1) throw new InvalidOperationException("sem rede"); state = UpdateState.Development; return Task.CompletedTask; },
            () => state,
            (wait, _) => { delays.Add(wait); return Task.CompletedTask; },
            () => 0,
            () => DateTime.UtcNow);

        await scheduler.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, checks);
        Assert.Equal([TimeSpan.FromMinutes(5)], delays);
    }

    [Fact]
    public async Task Scheduler_Wake_CutsTheWaitShortOnlyWhenTheRuleAllows()
    {
        var state = UpdateState.Failed;
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var checks = 0;
        var waiting = new SemaphoreSlim(0);
        // O adiamento só acaba quando o token é cancelado (Wake ou parar).
        var scheduler = new UpdateScheduler(
            () => { checks++; return Task.CompletedTask; },
            () => state,
            async (_, token) => { waiting.Release(); await Task.Delay(Timeout.Infinite, token); },
            () => 0,
            () => now);
        using var stop = new CancellationTokenSource();
        var run = scheduler.RunAsync(stop.Token);

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, checks);
        scheduler.Wake();                                // Failed: procura já
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, checks);

        state = UpdateState.UpToDate;
        now += TimeSpan.FromMinutes(10);
        scheduler.Wake();                                // atualizado há 10 minutos: nada
        await Task.Delay(100);
        Assert.Equal(2, checks);

        now += TimeSpan.FromMinutes(50);
        scheduler.Wake();                                // passou 1 hora desde a última procura
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, checks);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Wake_WithoutARunningWait_DoesNothing()
    {
        var scheduler = new UpdateScheduler(() => Task.CompletedTask, () => UpdateState.Failed, (_, _) => Task.CompletedTask, () => 0, () => DateTime.UtcNow);
        scheduler.Wake();
    }

    [Theory]
    [InlineData(UpdateState.Ready, false, true, UpdateState.Ready)] [InlineData(UpdateState.Ready, false, false, UpdateState.Ready)]
    [InlineData(UpdateState.Checking, true, false, UpdateState.Ready)] [InlineData(UpdateState.Checking, false, false, UpdateState.UpToDate)]
    [InlineData(UpdateState.Checking, false, true, UpdateState.Failed)] [InlineData(UpdateState.Failed, false, false, UpdateState.UpToDate)]
    [InlineData(UpdateState.Ready, true, false, UpdateState.Ready)]
    public void StateAfterCheck_KeepsReadyWhenALaterCheckFailsOrFindsNothing(UpdateState before, bool found, bool failed, UpdateState expected)
        => Assert.Equal(expected, UpdateService.StateAfterCheck(before, found, failed));

    [Fact]
    public void App_StartsTheScheduleAfterTheFirstPage()
    {
        var code = GoogleSyncTests.Src("Eteris.Browser", "App.xaml.cs");
        Assert.Contains("FirstPaint.App.Then(UpdateService.StartSchedule);", code);
        Assert.DoesNotContain("UpdateService.CheckAndDownloadAsync()", code);
    }

    [Fact]
    public void CheckAndDownload_NoLongerStopsAtAReadyVersion_AndNeverGoesThroughCheckingWithOne()
    {
        var code = GoogleSyncTests.Src("Eteris.Browser", "Updates", "UpdateService.cs");
        // O corpo vive na sobrecarga com quem cria o UpdateManager (o plano 1 tornou-o injetável para os testes).
        var body = code[code.IndexOf("internal static async Task CheckAndDownloadAsync(Func<UpdateManager?> createManager)", StringComparison.Ordinal)..];
        Assert.DoesNotContain("State == UpdateState.Ready ||", body);
        Assert.Contains("StateAfterCheck(before", body);
        Assert.Contains("before != UpdateState.Ready", body);
    }

    [Fact]
    public void Policy_SaysTheCadence_InBothLanguages()
    {
        var policy = Regex.Replace(File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "docs", "site", "privacidade.html"), Encoding.UTF8), @"\s+", " ");
        Assert.Contains("de 6 em 6 horas", policy);
        Assert.Contains("every 6 hours", policy);
        Assert.Contains("confere a assinatura", policy);
        Assert.Contains("verifies the signature", policy);
    }
}
