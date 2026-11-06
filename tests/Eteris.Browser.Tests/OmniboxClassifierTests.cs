using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class OmniboxClassifierTests
{
    private static readonly SearchEngine Engine = new("Teste", "https://busca.test/?q={q}");

    [Theory]
    [InlineData("https://github.com", "https://github.com")]
    [InlineData("http://example.com/a?b=1", "http://example.com/a?b=1")]
    [InlineData("github.com", "https://github.com")]
    [InlineData("  github.com/anthropics  ", "https://github.com/anthropics")]
    [InlineData("pt.wikipedia.org/wiki/Lisboa", "https://pt.wikipedia.org/wiki/Lisboa")]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("localhost", "http://localhost")]
    [InlineData("192.168.1.1", "http://192.168.1.1")]
    [InlineData("10.0.0.5:8080/admin", "http://10.0.0.5:8080/admin")]
    [InlineData("eteris://newtab", "eteris://newtab")]
    [InlineData("file:///C:/temp/a.html", "file:///C:/temp/a.html")]
    [InlineData("about:blank", "about:blank")]
    public void Classify_Address_Navigates(string input, string expected)
    {
        var result = OmniboxClassifier.Classify(input, Engine);
        Assert.False(result!.IsSearch);
        Assert.Equal(expected, result.Url);
    }

    [Theory]
    [InlineData("receita de bacalhau", "https://busca.test/?q=receita%20de%20bacalhau")]
    [InlineData("lisboa", "https://busca.test/?q=lisboa")]
    [InlineData("c# & .net", "https://busca.test/?q=c%23%20%26%20.net")]
    [InlineData("?github.com", "https://busca.test/?q=github.com")]
    [InlineData("o que é o github.com", "https://busca.test/?q=o%20que%20%C3%A9%20o%20github.com")]
    [InlineData("1.5", "https://busca.test/?q=1.5")]
    public void Classify_Query_Searches(string input, string expected)
    {
        var result = OmniboxClassifier.Classify(input, Engine);
        Assert.True(result!.IsSearch);
        Assert.Equal(expected, result.Url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox")]
    public void Classify_ScriptSchemes_AreNeverNavigated(string input)
        => Assert.True(OmniboxClassifier.Classify(input, Engine)!.IsSearch);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_Blank_ReturnsNull(string input)
        => Assert.Null(OmniboxClassifier.Classify(input, Engine));
}
