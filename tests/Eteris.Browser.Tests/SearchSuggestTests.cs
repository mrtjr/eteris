using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class SearchSuggestTests
{
    [Fact]
    public void DuckDuckGo_EndpointAndParsing()
    {
        Assert.Equal("https://duckduckgo.com/ac/?q=fado%20lisboa&type=list", SearchSuggest.EndpointFor(SearchEngine.DuckDuckGo, "fado lisboa"));
        Assert.Equal(["fado", "fado lisboa"], SearchSuggest.Parse("""["fa",["fado","fado lisboa"]]"""));
    }

    [Fact]
    public void Brave_UsesItsOwnEndpoint()
        => Assert.StartsWith("https://search.brave.com/api/suggest?q=", SearchSuggest.EndpointFor(SearchEngine.Brave, "x"));

    [Fact]
    public void Startpage_HasNoEndpoint()
        => Assert.Null(SearchSuggest.EndpointFor(SearchEngine.Startpage, "x"));

    // Com o Google, o que se escreve na barra não vai para a Google letra a letra: só a pesquisa feita (como no Startpage).
    [Fact]
    public void Google_HasNoEndpoint()
        => Assert.Null(SearchSuggest.EndpointFor(SearchEngine.Google, "x"));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"a":1}""")]
    public void BadResponses_GiveNothing(string body)
        => Assert.Empty(SearchSuggest.Parse(body));

    [Theory]
    [InlineData("fado", true)]
    [InlineData("github.com", false)]
    [InlineData("https://a.pt/x", false)]
    [InlineData("!w lisboa", false)]
    [InlineData("2+2", false)]
    [InlineData("a", false)]
    public void OnlyRealSearchesAreSent(string input, bool send)
        => Assert.Equal(send, SearchSuggest.ShouldQuery(input, SearchEngine.DuckDuckGo));

    [Fact]
    public void Merge_AddsSuggestionsAfterPrimaryAndSkipsTheTypedText()
    {
        var list = Suggestions.Build("fado", SearchEngine.DuckDuckGo, []);
        var merged = SearchSuggest.Merge(list, ["fado", "fado lisboa", "fado coimbra"], SearchEngine.DuckDuckGo);
        Assert.Equal(["fado", "fado lisboa", "fado coimbra"], merged.Select(s => s.Text));
        Assert.All(merged.Skip(1), s => Assert.Equal(SuggestionKind.Search, s.Kind));
    }
}
