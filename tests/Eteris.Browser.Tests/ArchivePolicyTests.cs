using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Arquivo automático: quem se arquiva, quando, com que opacidade; o repositório; a definição; a última visita na sessão.
public sealed class ArchivePolicyTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "eteris-archive-" + Guid.NewGuid() + ".db");
    private readonly string _json = Path.Combine(Path.GetTempPath(), "eteris-archive-" + Guid.NewGuid() + ".json");
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
        File.Delete(_json);
    }

    [Fact]
    public void ShouldArchive_OnlyAfterTheDeadline_AndNeverWhenOff()
    {
        Assert.False(ArchivePolicy.ShouldArchive(false, false, false, false, false, false, Now.AddDays(-6.9), Now, 7));
        Assert.True(ArchivePolicy.ShouldArchive(false, false, false, false, false, false, Now.AddDays(-7), Now, 7));
        Assert.False(ArchivePolicy.ShouldArchive(false, false, false, false, false, false, Now.AddDays(-40), Now, 0));
    }

    [Theory]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, false)]
    [InlineData(false, false, false, false, false, true)]
    public void Exempt_OnScreenPinnedLockedNotedAudibleOrNewTab(bool onScreen, bool pinned, bool locked, bool note, bool audible, bool newTab)
    {
        Assert.True(ArchivePolicy.IsExempt(onScreen, pinned, locked, note, audible, newTab));
        Assert.False(ArchivePolicy.ShouldArchive(onScreen, pinned, locked, note, audible, newTab, Now.AddDays(-30), Now, 7));
        Assert.Equal(1, ArchivePolicy.Opacity(exempt: true, Now.AddDays(-30), Now, 7));
    }

    [Fact]
    public void Opacity_FullUntilHalfway_ThenFadesToTheFloor()
    {
        Assert.Equal(1, ArchivePolicy.Opacity(false, Now, Now, 7));
        Assert.Equal(1, ArchivePolicy.Opacity(false, Now.AddDays(-3.5), Now, 7));
        var threeQuarters = ArchivePolicy.Opacity(false, Now.AddDays(-5.25), Now, 7);
        Assert.InRange(threeQuarters, 0.7, 0.75);
        Assert.Equal(ArchivePolicy.FadedOpacity, ArchivePolicy.Opacity(false, Now.AddDays(-7), Now, 7));
        Assert.Equal(ArchivePolicy.FadedOpacity, ArchivePolicy.Opacity(false, Now.AddDays(-70), Now, 7));
        Assert.Equal(1, ArchivePolicy.Opacity(false, Now.AddDays(-70), Now, 0));
    }

    [Fact]
    public void Ago_SpeaksInDays()
    {
        Assert.Equal("hoje", ArchivePolicy.Ago(Now.AddHours(-3), Now));
        Assert.Equal("ontem", ArchivePolicy.Ago(Now.AddDays(-1), Now));
        Assert.Equal("há 9 dias", ArchivePolicy.Ago(Now.AddDays(-9.5), Now));
    }

    [Fact]
    public void Store_NewestFirst_KeepsName_AndForgetsAfterThreeMonths()
    {
        var store = new ArchivedTabsStore(_db);
        store.Add("https://old.pt/", "Velha", null, Now.AddDays(-120), Now.AddDays(-100));
        store.Add("https://a.pt/", "A", "Loja", Now.AddDays(-9), Now.AddDays(-1));
        store.Add("https://b.pt/", "B", null, Now.AddDays(-8), Now);
        var all = new ArchivedTabsStore(_db).All();
        Assert.Equal(["B", "A"], all.Select(t => t.Title));
        Assert.Equal("Loja", all[1].Name);
        Assert.Equal(Now.AddDays(-9), all[1].LastActiveUtc);
        store.Delete(all[0].Id);
        Assert.Equal("A", Assert.Single(store.All()).Title);
    }

    [Fact]
    public void Radar_ArchiveScope_SaysWhenAndOffersRestore()
    {
        var vm = new TabsViewModel();
        vm.NewTab("https://x.pt/");
        var archived = new List<ArchivedTab> { new(1, "https://a.pt/artigo", "Artigo", "Ler", Now.AddDays(-9), Now.AddDays(-2)) };
        var rows = TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Archived, RadarSort.Recent, "", nowLocal: Now.ToLocalTime(), archived: archived);
        var row = Assert.Single(rows);
        Assert.Equal("Ler", row.Title);
        Assert.Equal("Arquivada há 2 dias · última visita há 9 dias", row.Status);
        Assert.Same(archived[0], row.Identity);
        Assert.Single(TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.Archived, RadarSort.Recent, "artigo", archived: archived));
        Assert.Contains("Arquivo", TabRadar.Status(0, RadarScope.Archived, ""));
    }

    [Fact]
    public void Setting_AcceptsDaysInRange()
    {
        Assert.Equal(ArchivePolicy.DefaultDays, new UserSettings().ArchiveDays);
        Assert.Equal(14, new UserSettings().With("archive", "14")!.ArchiveDays);
        Assert.Equal(0, new UserSettings().With("archive", "0")!.ArchiveDays);
        Assert.Null(new UserSettings().With("archive", "400"));
        Assert.Null(new UserSettings().With("archive", "x"));
    }

    [Fact]
    public void Session_KeepsTheLastVisit_SoAgeSurvivesARestart()
    {
        var vm = new TabsViewModel();
        vm.NewTab("https://a.pt/");
        var old = vm.NewTab("https://b.pt/", activate: false);
        old.LastActiveUtc = Now.AddDays(-5);
        var snapshot = vm.MetaSnapshot();
        Assert.NotNull(snapshot[1].Seen);
        var store = new SessionStore(_json);
        store.Save(["https://a.pt/", "https://b.pt/"], 0, metas: snapshot);
        var restored = new TabsViewModel();
        var session = store.Load()!;
        restored.Restore(session.Urls, 0, metas: session.Metas);
        Assert.Equal(Now.AddDays(-5), restored.Tabs[1].LastActiveUtc);
        // A ativa conta como vista agora, não como a última vez que deixou de o ser.
        Assert.True(restored.Tabs[0].LastActiveUtc > Now.AddDays(-1));
    }
}
