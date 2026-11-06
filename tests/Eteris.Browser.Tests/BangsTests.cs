using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class BangsTests
{
    private static readonly SearchEngine Ddg = SearchEngine.DuckDuckGo;

    [Theory]
    [InlineData("!w Lisboa", "https://pt.wikipedia.org/w/index.php?search=Lisboa", "Lisboa")]
    [InlineData("Lisboa !w", "https://pt.wikipedia.org/w/index.php?search=Lisboa", "Lisboa")]
    [InlineData("!yt fado amália", "https://www.youtube.com/results?search_query=fado%20am%C3%A1lia", "fado amália")]
    [InlineData("!gh cefsharp", "https://github.com/search?q=cefsharp", "cefsharp")]
    [InlineData("!map Porto", "https://www.openstreetmap.org/search?query=Porto", "Porto")]
    [InlineData("!B rust", "https://search.brave.com/search?q=rust", "rust")]
    public void Bang_SearchesOnTargetSite(string input, string url, string query)
    {
        var result = OmniboxClassifier.Classify(input, Ddg)!;
        Assert.True(result.IsSearch);
        Assert.Equal(url, result.Url);
        Assert.Equal(query, result.Query);
    }

    [Fact]
    public void Bang_ReportsSiteName()
        => Assert.Equal("Wikipédia", OmniboxClassifier.Classify("!w Lisboa", Ddg)!.Engine?.Name);

    [Fact]
    public void BangAlone_OpensSiteHome()
    {
        var result = OmniboxClassifier.Classify("!yt", Ddg)!;
        Assert.False(result.IsSearch);
        Assert.Equal("https://www.youtube.com/", result.Url);
    }

    [Theory]
    [InlineData("!naoexiste teste")]
    [InlineData("olá! tudo bem")]
    [InlineData("wow!w")]
    public void UnknownOrEmbeddedBang_IsNormalSearch(string input)
    {
        var result = OmniboxClassifier.Classify(input, Ddg)!;
        Assert.True(result.IsSearch);
        Assert.StartsWith("https://duckduckgo.com/", result.Url);
        Assert.Equal(input, result.Query);
    }

    [Fact]
    public void Suggestion_ShowsTargetSite()
    {
        var list = Suggestions.Build("!gh eteris", Ddg, []);
        Assert.Equal("eteris", list[0].Text);
        Assert.Equal("Pesquisar em GitHub", list[0].Detail);
    }

    [Fact]
    public void Bangs_AreListedForHelp()
        => Assert.Contains(Bangs.All, b => b.Trigger == "w");

    [Fact]
    public void TypingBangPrefix_ListsMatchingBangs()
    {
        var list = Suggestions.Build("!y", Ddg, []);
        Assert.Contains(list, s => s.Text == "!yt" && s.Detail == "YouTube" && s.Url == "https://www.youtube.com/");
        Assert.DoesNotContain(list, s => s.Text == "!gh");
    }
}
