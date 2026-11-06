using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class SuggestionsTests
{
    [Fact]
    public void HistorySuggestion_HasMonogram_SearchDoesNot()
    {
        Assert.Equal("P", new Suggestion(SuggestionKind.History, "Público", "https://www.publico.pt/", "https://www.publico.pt/").Monogram);
        Assert.Null(new Suggestion(SuggestionKind.Search, "x", "", "https://duckduckgo.com/?q=x").Monogram);
        Assert.StartsWith("#", new Suggestion(SuggestionKind.Bookmark, "a", "", "https://a.pt/").MonogramColor);
    }

    private static readonly SearchEngine Engine = new("Teste", "https://busca.test/?q={q}");

    [Fact]
    public void Build_Query_FirstEntryIsSearch()
    {
        var list = Suggestions.Build("bacalhau", Engine, []);
        Assert.Equal(SuggestionKind.Search, list[0].Kind);
        Assert.Equal("https://busca.test/?q=bacalhau", list[0].Url);
    }

    [Fact]
    public void Build_Address_FirstEntryIsNavigate()
    {
        var list = Suggestions.Build("github.com", Engine, []);
        Assert.Equal(SuggestionKind.Navigate, list[0].Kind);
        Assert.Equal("https://github.com", list[0].Url);
    }

    [Fact]
    public void Build_HistoryFollowsPrimaryAndSkipsDuplicateOfPrimary()
    {
        var history = new[]
        {
            new HistoryEntry("https://github.com", "GitHub", 3),
            new HistoryEntry("https://github.com/anthropics", "Anthropic", 1),
        };
        var list = Suggestions.Build("github.com", Engine, history);
        Assert.Equal(2, list.Count);
        Assert.Equal(SuggestionKind.History, list[1].Kind);
        Assert.Equal("https://github.com/anthropics", list[1].Url);
    }

    [Theory]
    [InlineData("git", "https://github.com/", "github.com")]
    [InlineData("GIT", "https://github.com/", "GIThub.com")]
    [InlineData("www.git", "https://www.github.com/", "www.github.com")]
    [InlineData("pt.wiki", "https://pt.wikipedia.org/wiki/Lisboa", "pt.wikipedia.org")]
    public void InlineCompletion_CompletesHostOfTopHistoryMatch(string typed, string historyUrl, string expected)
        => Assert.Equal(expected, Suggestions.InlineCompletion(typed, [new HistoryEntry(historyUrl, "", 1)]));

    [Theory]
    [InlineData("receita de")]
    [InlineData("hub")]
    [InlineData("github.com/a")]
    [InlineData("")]
    public void InlineCompletion_NoHostPrefix_ReturnsNull(string typed)
        => Assert.Null(Suggestions.InlineCompletion(typed, [new HistoryEntry("https://github.com/", "", 1)]));
}
