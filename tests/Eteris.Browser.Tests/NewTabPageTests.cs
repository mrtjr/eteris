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
}
