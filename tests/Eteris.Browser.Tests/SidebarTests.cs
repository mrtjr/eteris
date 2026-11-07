using Eteris.Browser.Data;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser.Tests;

// Barra lateral unificada (Ctrl+Shift+B): painéis disponíveis, larguras, lado e margens da página.
public class SidebarTests
{
    [Fact]
    public void BuiltIn_HasTheFourPanelsInOrder()
        => Assert.Equal(["notes", "reading", "bookmarks", "history"], SidebarPanels.BuiltIn.Select(p => p.Key));

    [Fact]
    public void PrivateWindow_KeepsOnlyReadingListAndBookmarks()
    {
        Assert.Equal(["reading", "bookmarks"], SidebarPanels.Available(isPrivate: true).Select(p => p.Key));
        Assert.Equal(SidebarPanels.BuiltIn, SidebarPanels.Available(isPrivate: false));
    }

    [Theory]
    [InlineData(double.NaN, 1600, 360)]
    [InlineData(0, 1600, 360)]
    [InlineData(100, 1600, 280)]
    [InlineData(2000, 1600, 720)]
    [InlineData(500, 1600, 500)]
    [InlineData(500, 800, 400)]
    [InlineData(500, 400, 280)]
    public void ClampWidth_StaysBetweenTheLimitsAndHalfTheWindow(double wanted, double window, double expected)
        => Assert.Equal(expected, SidebarPanels.ClampWidth(wanted, window));

    [Fact]
    public void Resize_RightSideGrowsWhenDraggingLeft_LeftSideGrowsWhenDraggingRight()
    {
        Assert.Equal(380, SidebarPanels.Resize(360, -20, SidebarSide.Right, 1600));
        Assert.Equal(340, SidebarPanels.Resize(360, 20, SidebarSide.Right, 1600));
        Assert.Equal(380, SidebarPanels.Resize(360, 20, SidebarSide.Left, 1600));
        Assert.Equal(280, SidebarPanels.Resize(290, 50, SidebarSide.Right, 1600));
    }

    [Fact]
    public void Resolve_PrefersTheWantedPanelAndFallsBackToTheFirst()
    {
        var available = SidebarPanels.Available(isPrivate: true);
        Assert.Equal("bookmarks", SidebarPanels.Resolve("bookmarks", available)!.Key);
        Assert.Equal("reading", SidebarPanels.Resolve("notes", available)!.Key);
        Assert.Equal("reading", SidebarPanels.Resolve(null, available)!.Key);
        Assert.Null(SidebarPanels.Resolve("notes", []));
    }

    [Fact]
    public void PageMargins_FollowTheSideAndTheVerticalTabs()
    {
        Assert.Equal((0, 0), SidebarPanels.PageMargins(0, open: false, SidebarSide.Right, 360));
        Assert.Equal((0, 354), SidebarPanels.PageMargins(0, open: true, SidebarSide.Right, 360));
        Assert.Equal((354, 0), SidebarPanels.PageMargins(0, open: true, SidebarSide.Left, 360));
        // Abas verticais (248 px) à esquerda: a página já desconta 242; a barra lateral soma a sua largura menos a folga.
        Assert.Equal((242, 0), SidebarPanels.PageMargins(248, open: false, SidebarSide.Left, 360));
        Assert.Equal((242 + 354, 0), SidebarPanels.PageMargins(248, open: true, SidebarSide.Left, 360));
        Assert.Equal((242, 354), SidebarPanels.PageMargins(248, open: true, SidebarSide.Right, 360));
    }

    [Theory]
    [InlineData("left", SidebarSide.Left)]
    [InlineData("right", SidebarSide.Right)]
    [InlineData(null, SidebarSide.Right)]
    [InlineData("top", SidebarSide.Right)]
    public void ParseSide_DefaultsToRight(string? value, SidebarSide expected)
    {
        Assert.Equal(expected, SidebarPanels.ParseSide(value));
        Assert.Equal(expected, SidebarPanels.ParseSide(SidebarPanels.SideName(expected)));
    }

    [Theory]
    [InlineData("notes", true)]
    [InlineData("history", true)]
    [InlineData("web:https://pt.wikipedia.org/", true)]
    [InlineData("web:http://127.0.0.1:8765/", true)]
    [InlineData("web:javascript:alert(1)", false)]
    [InlineData("web:eteris://settings/", false)]
    [InlineData("web:", false)]
    [InlineData("tabs", false)]
    [InlineData(null, false)]
    public void IsKnownKey_AcceptsBuiltInPanelsAndWebSites(string? key, bool expected)
        => Assert.Equal(expected, SidebarPanels.IsKnownKey(key));

    [Theory]
    [InlineData("sidebar", "true")]
    [InlineData("sidebarPanel", "history")]
    [InlineData("sidebarPanel", "web:https://a.pt/")]
    [InlineData("sidebarWidth", "280")]
    [InlineData("sidebarWidth", "720")]
    [InlineData("sidebarSide", "left")]
    public void Settings_AcceptTheSidebarKeys(string key, string value)
        => Assert.NotNull(new UserSettings().With(key, value));

    [Theory]
    [InlineData("sidebar", "sim")]
    [InlineData("sidebarPanel", "nope")]
    [InlineData("sidebarPanel", "web:ftp://a.pt/")]
    [InlineData("sidebarWidth", "100")]
    [InlineData("sidebarWidth", "10000")]
    [InlineData("sidebarSide", "top")]
    public void Settings_RejectBadSidebarValues(string key, string value)
        => Assert.Null(new UserSettings().With(key, value));

    [Fact]
    public void Settings_DefaultToClosedNotesOnTheRight()
    {
        var s = new UserSettings();
        Assert.False(s.Sidebar);
        Assert.Equal("notes", s.SidebarPanel);
        Assert.Equal(360, s.SidebarWidth);
        Assert.Equal("right", s.SidebarSide);
    }

    // ---------- Listas ----------

    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reading_KeepsTheOrderAndMarksReadItems()
    {
        var rows = SidebarLists.Reading(
        [
            new ReadingItem("https://www.exemplo.pt/artigo", "Um artigo", Now.AddDays(-2), Read: false),
            new ReadingItem("https://outro.pt/", "", Now.AddHours(-3), Read: true),
        ], Now);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Um artigo", rows[0].Title);
        Assert.Equal("exemplo.pt", rows[0].Host);
        Assert.Equal("há 2 dias", rows[0].Detail);
        Assert.False(rows[0].Muted);
        Assert.Equal("E", rows[0].Letter);
        Assert.StartsWith("#", rows[0].Color);
        // Sem título: o endereço legível; lido: esbatido com «Lido».
        Assert.Equal("outro.pt", rows[1].Title);
        Assert.Equal("Lido", rows[1].Detail);
        Assert.True(rows[1].Muted);
    }

    [Theory]
    [InlineData(0, "agora mesmo")]
    [InlineData(5, "há 5 min")]
    [InlineData(59, "há 59 min")]
    [InlineData(60, "há 1 h")]
    [InlineData(23 * 60, "há 23 h")]
    [InlineData(25 * 60, "ontem")]
    [InlineData(4 * 24 * 60, "há 4 dias")]
    public void Ago_ReadsNaturally(int minutesAgo, string expected)
        => Assert.Equal(expected, SidebarLists.Ago(Now.AddMinutes(-minutesAgo), Now));

    [Fact]
    public void Ago_AfterAMonthShowsTheDate()
        => Assert.Matches(@"^\d{1,2} \w+\.? 2026$", SidebarLists.Ago(Now.AddDays(-45), Now));

    [Fact]
    public void TitleOf_CollapsesSpacesAndFallsBackToTheAddress()
    {
        Assert.Equal("Fado – Wikipédia", SidebarLists.TitleOf("Fado –   Wikipédia ", "https://pt.wikipedia.org/wiki/Fado"));
        Assert.Equal("pt.wikipedia.org/wiki/Fado", SidebarLists.TitleOf(null, "https://pt.wikipedia.org/wiki/Fado/"));
        Assert.Equal("a.pt", SidebarLists.TitleOf("", "https://a.pt/"));
    }
}
