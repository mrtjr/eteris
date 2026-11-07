using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// «Trancar» (não fecha por engano nem adormece) e «Feito» (sai da faixa e fica em «Feitas hoje»).
public sealed class TabLockDoneTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "eteris-done-" + Guid.NewGuid() + ".db");
    private readonly string _json = Path.Combine(Path.GetTempPath(), "eteris-lock-" + Guid.NewGuid() + ".json");
    private static readonly DateTime Now = new(2026, 11, 7, 15, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
        File.Delete(_json);
    }

    private static TabsViewModel With(int count)
    {
        var vm = new TabsViewModel();
        for (var i = 0; i < count; i++) vm.NewTab($"https://s{i}.pt/");
        return vm;
    }

    [Fact]
    public void Close_RefusesALockedTab()
    {
        var vm = With(2);
        var locked = vm.Tabs[0];
        locked.IsLocked = true;
        Assert.False(vm.Close(locked));
        Assert.Contains(locked, vm.Tabs);
        Assert.Empty(vm.RecentlyClosed);
        locked.IsLocked = false;
        Assert.False(vm.Close(locked));
        Assert.DoesNotContain(locked, vm.Tabs);
    }

    [Fact]
    public void CloseOthersAndToRight_KeepLockedTabs()
    {
        var vm = With(4);
        vm.Tabs[1].IsLocked = true;
        vm.Tabs[3].IsLocked = true;
        vm.CloseOthers(vm.Tabs[0]);
        Assert.Equal(["https://s0.pt/", "https://s1.pt/", "https://s3.pt/"], vm.Tabs.Select(t => t.Address));
        vm.CloseToRight(vm.Tabs[0]);
        Assert.Equal(3, vm.Tabs.Count);
    }

    [Fact]
    public void CloseDone_ClosesEvenLocked_WithoutRememberingIt()
    {
        var vm = With(2);
        var tab = vm.Tabs[1];
        tab.IsLocked = true;
        vm.CloseDone(tab);
        Assert.DoesNotContain(tab, vm.Tabs);
        Assert.Empty(vm.RecentlyClosed);
    }

    [Fact]
    public void LockedTab_NeverSleeps()
    {
        Assert.True(SleepPolicy.ShouldSleep(false, false, false, false, Now.AddHours(-2), Now));
        Assert.False(SleepPolicy.ShouldSleep(false, false, false, false, Now.AddHours(-2), Now, isLocked: true));
    }

    [Fact]
    public void Lock_ShowsInStatusAndRadar_AndSurvivesTheSession()
    {
        var vm = With(1);
        vm.Tabs[0].IsLocked = true;
        Assert.Contains("Trancada", vm.Tabs[0].StatusLine);
        Assert.Contains("Trancada", TabRadar.FromTab(vm.Tabs[0], active: false, duplicates: 0).Status);

        var store = new SessionStore(_json);
        store.Save(["https://a.pt/"], 0, metas: vm.MetaSnapshot());
        var session = store.Load()!;
        Assert.True(session.Metas![0].Locked);
        var restored = new TabsViewModel();
        restored.Restore(session.Urls, 0, metas: session.Metas);
        Assert.True(restored.Tabs[0].IsLocked);
    }

    [Fact]
    public void DoneStore_ListsTodayNewestFirst_AndForgetsAfterAMonth()
    {
        var store = new DoneTabsStore(_db);
        store.Add("https://old.pt/", "Antiga", Now.AddDays(-40));
        store.Add("https://a.pt/", "A", Now.AddHours(-3));
        store.Add("https://b.pt/", "B", Now.AddMinutes(-5));
        var today = store.Since(Now.AddHours(-12));
        Assert.Equal(["B", "A"], today.Select(t => t.Title));
        Assert.Equal(Now.AddMinutes(-5), today[0].DoneUtc);
        // A antiga foi apagada na inserção seguinte (mais de 30 dias).
        Assert.DoesNotContain(store.Since(DateTime.MinValue.AddYears(1)), t => t.Title == "Antiga");
        store.Delete(today[0].Id);
        Assert.Single(store.Since(Now.AddHours(-12)));
    }

    [Fact]
    public void StartOfLocalDay_IsMidnightLocalInUtc()
    {
        var local = new DateTime(2026, 11, 7, 15, 30, 0, DateTimeKind.Local);
        var start = DoneTabsStore.StartOfLocalDayUtc(local);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
        Assert.Equal(new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), start);
    }

    [Fact]
    public void Radar_DoneScope_ListsDoneTabs_WithReopenIdentity()
    {
        var vm = With(1);
        var done = new List<DoneTab> { new(1, "https://x.pt/artigo", "Artigo", Now), new(2, "https://y.pt/", "", Now.AddMinutes(-1)) };
        var rows = TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Done, RadarSort.Recent, "", done);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Artigo", rows[0].Title);
        Assert.Equal("y.pt", rows[1].Title);
        Assert.Same(done[0], rows[0].Identity);
        Assert.StartsWith("Feita às", rows[0].Status);
        Assert.Single(TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Done, RadarSort.Recent, "artigo", done));
        Assert.Empty(TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Done, RadarSort.Recent, ""));
        Assert.Contains("Feito", TabRadar.Status(0, RadarScope.Done, ""));
    }
}
