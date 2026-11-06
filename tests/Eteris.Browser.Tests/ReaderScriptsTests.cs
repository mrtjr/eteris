using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ReaderScriptsTests
{
    [Theory]
    [InlineData("https://www.publico.pt/artigo", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("eteris://history/", false)]
    [InlineData("file:///C:/a.html", false)]
    public void OnlyWebPagesAreCandidates(string url, bool expected)
        => Assert.Equal(expected, ReaderScripts.IsCandidate(url));

    [Fact]
    public void Toggle_IsolatesStylesFromThePage()
    {
        Assert.Contains("attachShadow", ReaderScripts.Toggle);
        Assert.Contains("adoptedStyleSheets", ReaderScripts.Toggle);
        Assert.DoesNotContain("innerHTML", ReaderScripts.Toggle);
    }
}
