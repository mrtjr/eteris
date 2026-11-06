using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class NewTabPageTests
{
    private static readonly DateTime Afternoon = new(2026, 11, 5, 15, 0, 0);

    [Fact]
    public void Render_IsHtmlDocumentWithTitle()
    {
        var html = NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon);
        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<title>Nova aba</title>", html);
    }

    [Fact]
    public void Render_EmbedsTopSitesAndSearchTemplate()
    {
        var html = NewTabPage.Render([new TopSite("github.com", "https://github.com/", "GitHub", 3)], SearchEngine.DuckDuckGo, Afternoon);
        Assert.Contains("github.com", html);
        Assert.Contains("duckduckgo.com", html);
        Assert.Contains("Boa tarde", html);
    }

    [Fact]
    public void Render_HistoryTitleCannotBreakOutOfScript()
    {
        var evil = new TopSite("x.pt", "https://x.pt/", "</script><script>alert(1)</script>", 1);
        var html = NewTabPage.Render([evil], SearchEngine.DuckDuckGo, Afternoon);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("</script><script>", html);
    }

    [Fact]
    public void Render_Private_HidesSitesAndBookmarksAndSaysSo()
    {
        var html = NewTabPage.Render([new TopSite("github.com", "https://github.com/", "GitHub", 3)], SearchEngine.DuckDuckGo,
            Afternoon, bookmarks: [new Bookmark("https://mdn.dev/", "MDN Docs")], isPrivate: true);
        Assert.Contains("\"private\":true", html);
        Assert.DoesNotContain("github.com", html);
        Assert.DoesNotContain("MDN Docs", html);
    }

    [Theory]
    [InlineData("eteris://newtab/?private", true)]
    [InlineData("eteris://newtab/", false)]
    public void IsPrivateNewTab(string url, bool expected) => Assert.Equal(expected, NewTabPage.IsPrivate(url));

    [Fact]
    public void Render_IncludesBookmarks()
    {
        var html = NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon, bookmarks: [new Bookmark("https://mdn.dev/", "MDN Docs")]);
        Assert.Contains("MDN Docs", html);
        Assert.Contains("\"bookmarks\":[", html);
    }

    [Fact]
    public void Render_IncludesSessionBlockedCount()
        => Assert.Contains("\"blocked\":42", NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon, blocked: 42));

    [Fact]
    public void Render_TemplatePlaceholdersAreAllReplaced()
        => Assert.DoesNotContain("{{", NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon));

    [Theory]
    [InlineData(6, "Bom dia")]
    [InlineData(11, "Bom dia")]
    [InlineData(12, "Boa tarde")]
    [InlineData(19, "Boa tarde")]
    [InlineData(20, "Boa noite")]
    [InlineData(3, "Boa noite")]
    public void Greeting_FollowsPortugueseTimeOfDay(int hour, string expected)
        => Assert.Equal(expected, NewTabPage.Greeting(hour));

    [Theory]
    [InlineData("eteris://newtab/", true)]
    [InlineData("eteris://newtab", true)]
    [InlineData("ETERIS://NEWTAB/", true)]
    [InlineData("https://newtab.pt/", false)]
    [InlineData("about:blank", false)]
    public void IsNewTab_RecognisesOnlyTheNewTabPage(string address, bool expected)
        => Assert.Equal(expected, NewTabPage.IsNewTab(address));

    private static readonly DateTime T0 = new(2026, 11, 6, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resume_TwoSessionsThenUnreadArticlesUpToFour()
    {
        var sessions = Enumerable.Range(1, 3).Select(i => new SavedSession(i, $"S{i}", T0, [new SavedTab("https://a.pt/", "A")])).ToList();
        var reading = new List<ReadingItem>
        {
            new("https://r1.pt/x", "R1", T0, false),
            new("https://r2.pt/x", "R2", T0, true),
            new("https://r3.pt/x", "", T0, false),
            new("https://r4.pt/x", "R4", T0, false),
        };
        var cards = NewTabPage.Resume(reading, sessions);
        Assert.Equal(["S1", "S2", "R1", "https://r3.pt/x"], cards.Select(c => c.Title));
        Assert.Equal("Sessão · 1 aba", cards[0].Subtitle);
        Assert.Equal("Para ler · r1.pt", cards[2].Subtitle);
        Assert.Equal(2, cards[1].SessionId);
    }

    [Fact]
    public void Render_PrivateWindowHidesResumeCards()
    {
        var html = NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon, isPrivate: true,
            reading: [new ReadingItem("https://segredo.pt/", "Segredo", T0, false)],
            sessions: [new SavedSession(1, "Trabalho", T0, [new SavedTab("https://a.pt/", "A")])]);
        Assert.DoesNotContain("segredo.pt", html);
        Assert.DoesNotContain("Trabalho", html);
        Assert.Contains("\"resume\":[]", html);
    }

    [Fact]
    public void Tip_ChangesDailyAndHidesWhenTurnedOffOrPrivate()
    {
        var today = NewTabPage.TipFor(new DateTime(2026, 11, 6, 8, 0, 0));
        Assert.Equal(today, NewTabPage.TipFor(new DateTime(2026, 11, 6, 23, 59, 0)));
        Assert.NotEqual(today, NewTabPage.TipFor(new DateTime(2026, 11, 7, 8, 0, 0)));
        Assert.Contains("Sabia que", NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon));
        Assert.Contains("\"tip\":null", NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon, isPrivate: true));
        Assert.Contains("\"tip\":null", NewTabPage.Render([], SearchEngine.DuckDuckGo, Afternoon, prefs: new UserSettings { NewTabTips = false }));
    }
}
