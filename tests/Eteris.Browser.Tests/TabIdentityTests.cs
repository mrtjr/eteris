using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public sealed class TabIdentityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-identity-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void NormalizeName_TrimsCollapsesAndCaps()
    {
        Assert.Null(TabIdentity.NormalizeName(null));
        Assert.Null(TabIdentity.NormalizeName("   "));
        Assert.Equal("Comparar preços", TabIdentity.NormalizeName("  Comparar \n  preços  "));
        var long_ = new string('a', TabIdentity.MaxNameLength + 10);
        Assert.Equal(TabIdentity.MaxNameLength, TabIdentity.NormalizeName(long_)!.Length);
    }

    [Fact]
    public void NormalizeEmojiAndColor_OnlyAcceptKnownValues()
    {
        Assert.Equal("📌", TabIdentity.NormalizeEmoji("📌"));
        Assert.Null(TabIdentity.NormalizeEmoji("texto"));
        Assert.Null(TabIdentity.NormalizeEmoji(null));
        Assert.Equal(3, TabIdentity.NormalizeColor(3));
        Assert.Null(TabIdentity.NormalizeColor(TabGroup.Palette.Length));
        Assert.Null(TabIdentity.NormalizeColor(-1));
        Assert.Null(TabIdentity.NormalizeColor(null));
    }

    [Fact]
    public void DisplayTitle_PrefersCustomThenTitleThenHost()
    {
        Assert.Equal("Loja", TabIdentity.DisplayTitle("Loja", "Página", "a.pt"));
        Assert.Equal("Página", TabIdentity.DisplayTitle(null, "Página", "a.pt"));
        Assert.Equal("a.pt", TabIdentity.DisplayTitle(null, " ", "a.pt"));
    }

    [Fact]
    public void KeepsIdentity_WithinSiteAndFromNewTab()
    {
        Assert.True(TabIdentity.KeepsIdentity("https://a.pt/x", "https://a.pt/y?z"));
        Assert.True(TabIdentity.KeepsIdentity("https://A.pt/", "https://a.pt/outra"));
        Assert.True(TabIdentity.KeepsIdentity(Browser.NewTabPage.Url, "https://a.pt/"));
        Assert.False(TabIdentity.KeepsIdentity("https://a.pt/", "https://b.pt/"));
        Assert.False(TabIdentity.KeepsIdentity("https://a.pt/", Browser.NewTabPage.Url));
    }

    [Fact]
    public void Tab_KeepsNameOnSameSite_DropsItOnAnotherSite()
    {
        var tab = new BrowserTab("https://a.pt/1") { Title = "Página 1" };
        tab.SetIdentity("Comparar", "⭐", 2);
        Assert.Equal("Comparar", tab.DisplayTitle);
        Assert.True(tab.HasCustomIcon);
        Assert.Equal("⭐", tab.IconText);
        tab.Address = "https://a.pt/2";
        Assert.Equal("Comparar", tab.CustomTitle);
        tab.Address = "https://b.pt/";
        Assert.Null(tab.CustomTitle);
        Assert.Null(tab.Emoji);
        Assert.Null(tab.ColorIndex);
        Assert.False(tab.HasCustomIcon);
        Assert.Equal("Página 1", tab.DisplayTitle);
    }

    [Fact]
    public void Tab_NamedWhileOnNewTab_KeepsTheNameOnFirstNavigation()
    {
        var tab = new BrowserTab(Browser.NewTabPage.Url);
        tab.SetIdentity("Projeto", null, 1);
        tab.Address = "https://a.pt/";
        Assert.Equal("Projeto", tab.CustomTitle);
        Assert.Equal(1, tab.ColorIndex);
        // Sem emoji, o ícone colorido mostra a letra do site.
        Assert.Equal("A", tab.IconText);
    }

    [Fact]
    public void NormalizeNote_OneShortLine()
    {
        Assert.Null(TabIdentity.NormalizeNote(" \n "));
        Assert.Equal("comparar com a outra loja", TabIdentity.NormalizeNote("comparar com\na outra   loja "));
        Assert.Equal(TabIdentity.MaxNoteLength, TabIdentity.NormalizeNote(new string('x', 500))!.Length);
    }

    [Fact]
    public void Tab_Note_FollowsTheSameSiteRule()
    {
        var tab = new BrowserTab("https://a.pt/1");
        tab.Note = TabIdentity.NormalizeNote("porque sim");
        Assert.True(tab.HasNote);
        tab.Address = "https://a.pt/2";
        Assert.Equal("porque sim", tab.Note);
        tab.Address = "https://b.pt/";
        Assert.Null(tab.Note);
        Assert.False(tab.HasNote);
    }

    [Fact]
    public void Radar_ShowsTheNote_AndFindsTabsByIt()
    {
        var vm = new TabsViewModel();
        var tab = vm.NewTab("https://a.pt/");
        tab.Title = "Loja";
        tab.Note = "comparar preços";
        vm.NewTab("https://b.pt/");
        var rows = TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.All, RadarSort.Strip, "");
        Assert.Equal("comparar preços", rows[0].Note);
        Assert.Null(rows[1].Note);
        var found = TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.All, RadarSort.Strip, "precos");
        Assert.Same(tab, Assert.Single(found).Tab);
    }

    [Fact]
    public void Session_RoundTripsTheNote()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/"], 0, metas: [new SessionTabMeta(Note: "ler amanhã")]);
        Assert.Equal("ler amanhã", store.Load()!.Metas![0].Note);
        var vm = new TabsViewModel();
        vm.Restore(["https://a.pt/"], 0, metas: [new SessionTabMeta(Note: "ler amanhã")]);
        Assert.Equal("ler amanhã", vm.Tabs[0].Note);
        Assert.Equal("ler amanhã", vm.MetaSnapshot()[0].Note);
    }

    [Fact]
    public void Tab_SetIdentity_NormalizesEverything()
    {
        var tab = new BrowserTab("https://a.pt/");
        tab.SetIdentity("  ", "não é emoji", 99);
        Assert.Null(tab.CustomTitle);
        Assert.Null(tab.Emoji);
        Assert.Null(tab.ColorIndex);
    }

    [Fact]
    public void Radar_ShowsCustomName_ButStillFindsThePageTitle()
    {
        var vm = new TabsViewModel();
        var tab = vm.NewTab("https://a.pt/");
        tab.Title = "Relatório anual";
        tab.SetIdentity("Ler amanhã", null, null);
        var rows = TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.All, RadarSort.Strip, "");
        Assert.Equal("Ler amanhã", rows.Single().Title);
        Assert.Single(TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.All, RadarSort.Strip, "relatorio"));
        Assert.Single(TabRadar.Rows(vm.Tabs, vm.Active, vm.RecentlyClosed, RadarScope.All, RadarSort.Strip, "amanha"));
    }

    [Fact]
    public void Session_RoundTripsIdentity_AlignedWithKeptTabs()
    {
        var store = new SessionStore(_path);
        store.Save(["javascript:x", "https://a.pt/", "https://b.pt/"], 1, metas: [null, new SessionTabMeta("Loja", "🛒", 4), SessionTabMeta.Empty]);
        var session = store.Load()!;
        Assert.Equal(2, session.Metas!.Count);
        Assert.Equal(new SessionTabMeta("Loja", "🛒", 4), session.Metas[0]);
        Assert.True(session.Metas[1].IsEmpty);
    }

    [Fact]
    public void Session_WithoutAnyIdentity_OmitsTheField_AndOldFilesStillLoad()
    {
        var store = new SessionStore(_path);
        store.Save(["https://a.pt/"], 0);
        Assert.DoesNotContain("Metas", File.ReadAllText(_path));
        var session = store.Load()!;
        Assert.True(session.Metas!.All(m => m.IsEmpty));
    }

    [Fact]
    public void Restore_AppliesIdentity_AndSnapshotGivesItBack()
    {
        var vm = new TabsViewModel();
        vm.Restore(["https://a.pt/", "https://b.pt/"], 0, metas: [new SessionTabMeta("Loja", "🛒", 4), SessionTabMeta.Empty]);
        Assert.Equal("Loja", vm.Tabs[0].DisplayTitle);
        Assert.Equal("🛒", vm.Tabs[0].Emoji);
        Assert.Null(vm.Tabs[1].CustomTitle);
        var snapshot = vm.MetaSnapshot();
        Assert.Equal(new SessionTabMeta("Loja", "🛒", 4), snapshot[0]);
        Assert.True(snapshot[1].IsEmpty);
    }
}
