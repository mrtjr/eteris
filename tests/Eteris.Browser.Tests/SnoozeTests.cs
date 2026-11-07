using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// «Adiar até…»: opções pela hora do dia, textos, vencidas e o repositório.
public sealed class SnoozeTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "eteris-snooze-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    // Quinta-feira, 5 de novembro de 2026, 10:15.
    private static readonly DateTime ThursdayMorning = new(2026, 11, 5, 10, 15, 0, DateTimeKind.Local);

    [Fact]
    public void Options_InTheMorning_OfferEveningTomorrowMondayAndWeek()
    {
        var options = Snooze.Options(ThursdayMorning);
        Assert.Equal(["Daqui a 1 hora", "Hoje à noite", "Amanhã de manhã", "Segunda de manhã", "Daqui a uma semana"], options.Select(o => o.Label));
        Assert.Equal(ThursdayMorning.AddHours(1), options[0].DueLocal);
        Assert.Equal(new DateTime(2026, 11, 5, 19, 0, 0), options[1].DueLocal);
        Assert.Equal(new DateTime(2026, 11, 6, 9, 0, 0), options[2].DueLocal);
        Assert.Equal(new DateTime(2026, 11, 9, 9, 0, 0), options[3].DueLocal);
        Assert.Equal(DayOfWeek.Monday, options[3].DueLocal.DayOfWeek);
        Assert.Equal(new DateTime(2026, 11, 12, 9, 0, 0), options[4].DueLocal);
        Assert.Equal("11:15", options[0].When);
    }

    [Fact]
    public void Options_LateInTheDay_SkipTonight()
    {
        var evening = new DateTime(2026, 11, 5, 18, 45, 0, DateTimeKind.Local);
        Assert.DoesNotContain(Snooze.Options(evening), o => o.Label == "Hoje à noite");
        Assert.Contains(Snooze.Options(new DateTime(2026, 11, 5, 18, 29, 0)), o => o.Label == "Hoje à noite");
    }

    [Fact]
    public void Options_OnSunday_MondayIsTomorrow_SoItIsNotRepeated()
    {
        var sunday = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Local);
        var labels = Snooze.Options(sunday).Select(o => o.Label).ToList();
        Assert.Contains("Amanhã de manhã", labels);
        Assert.DoesNotContain("Segunda de manhã", labels);
        // Numa segunda-feira, a próxima segunda é «Daqui a uma semana»: não aparece duas vezes com nomes diferentes.
        var monday = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Local);
        var options = Snooze.Options(monday);
        Assert.DoesNotContain(options, o => o.Label == "Segunda de manhã");
        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0), options.Single(o => o.Label == "Daqui a uma semana").DueLocal);
        Assert.Equal(options.Count, options.Select(o => o.DueLocal).Distinct().Count());

    }

    [Fact]
    public void Describe_SpeaksInDays()
    {
        Assert.Equal("hoje às 19:00", Snooze.Describe(new DateTime(2026, 11, 5, 19, 0, 0), ThursdayMorning));
        Assert.Equal("amanhã às 09:00", Snooze.Describe(new DateTime(2026, 11, 6, 9, 0, 0), ThursdayMorning));
        Assert.Equal("segunda às 09:00", Snooze.Describe(new DateTime(2026, 11, 9, 9, 0, 0), ThursdayMorning));
        Assert.Equal("12 nov às 09:00", Snooze.Describe(new DateTime(2026, 11, 12, 9, 0, 0), ThursdayMorning));
    }

    [Fact]
    public void Due_OnlyThoseAlreadyPast()
    {
        var now = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);
        var items = new[] { ("a", now.AddMinutes(-1)), ("b", now), ("c", now.AddMinutes(1)) };
        Assert.Equal(["a", "b"], Snooze.Due(items, i => i.Item2, now).Select(i => i.Item1));
    }

    [Fact]
    public void Store_KeepsNameAndNote_OrdersByDue_AndForgets()
    {
        var store = new SnoozedTabsStore(_db);
        var now = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);
        store.Add("https://b.pt/", "B", null, null, now.AddHours(2));
        store.Add("https://a.pt/", "A", "Loja", "comparar", now.AddMinutes(-5));
        var all = new SnoozedTabsStore(_db).All();
        Assert.Equal(["A", "B"], all.Select(t => t.Title));
        Assert.Equal(("Loja", "comparar"), (all[0].Name, all[0].Note));
        Assert.Null(all[1].Name);
        Assert.Equal(now.AddMinutes(-5), all[0].DueUtc);
        var due = store.Due(now);
        Assert.Equal("A", Assert.Single(due).Title);
        store.Delete(due[0].Id);
        Assert.Equal("B", Assert.Single(store.All()).Title);
    }

    [Fact]
    public void Radar_SnoozedScope_ShowsWhenItComesBack()
    {
        var vm = new TabsViewModel();
        vm.NewTab("https://x.pt/");
        var nowLocal = ThursdayMorning;
        var dueLocal = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Local);
        var snoozed = new List<SnoozedTab> { new(1, "https://a.pt/", "A", "Loja", "comparar", dueLocal.ToUniversalTime()) };
        var rows = TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Snoozed, RadarSort.Recent, "", null, snoozed, nowLocal);
        var row = Assert.Single(rows);
        Assert.Equal("Loja", row.Title);
        Assert.Equal("Volta amanhã às 09:00", row.Status);
        Assert.Equal("comparar", row.Note);
        Assert.Same(snoozed[0], row.Identity);
        Assert.Contains("Adiar", TabRadar.Status(0, RadarScope.Snoozed, ""));
    }

    [Fact]
    public void CloseQuietly_LeavesNoTraceInRecentlyClosed()
    {
        var vm = new TabsViewModel();
        vm.NewTab("https://a.pt/");
        var tab = vm.NewTab("https://b.pt/");
        tab.IsLocked = true;
        vm.CloseQuietly(tab);
        Assert.DoesNotContain(tab, vm.Tabs);
        Assert.Empty(vm.RecentlyClosed);
    }
}
