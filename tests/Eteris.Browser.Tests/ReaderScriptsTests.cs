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
    public void Toggle_ReadsAloudOnlyWithLocalVoicesAndStopsOnClose()
    {
        Assert.Contains("speechSynthesis", ReaderScripts.Toggle);
        Assert.Contains("localService", ReaderScripts.Toggle);
        Assert.Contains("stopSpeech(); host.remove()", ReaderScripts.Toggle);
        Assert.Contains("speechSynthesis.cancel(); open.remove()", ReaderScripts.Toggle);
    }

    [Fact]
    public void Toggle_IsolatesStylesFromThePage()
    {
        Assert.Contains("attachShadow", ReaderScripts.Toggle);
        Assert.Contains("adoptedStyleSheets", ReaderScripts.Toggle);
        Assert.DoesNotContain("innerHTML", ReaderScripts.Toggle);
    }

    [Fact]
    public void ToggleWith_StartsWithTheRememberedLook()
    {
        var script = ReaderScripts.ToggleWith("sepia", 23, false);
        Assert.Contains("let theme = 'sepia';", script);
        Assert.Contains("let size = 23;", script);
        Assert.Contains("(false ? ' serif' : ' sans')", script);
        Assert.DoesNotContain("__", ReaderScripts.ToggleWith("<script>", 99, true).Replace("__proto__", ""));
        Assert.Contains("let theme = 'dark';", ReaderScripts.ToggleWith("<script>", 99, true));
        Assert.Contains("let size = 28;", ReaderScripts.ToggleWith("dark", 99, true));
    }

    [Theory]
    [InlineData("eteris-reader {\"theme\":\"light\",\"size\":18,\"serif\":false}", "light", 18, false)]
    [InlineData("eteris-reader {\"theme\":\"sepia\",\"size\":28,\"serif\":true}", "sepia", 28, true)]
    public void ParseReport_ReadsValidPreferences(string message, string theme, int size, bool serif)
        => Assert.Equal((theme, size, serif), ReaderScripts.ParseReport(message));

    [Theory]
    [InlineData("eteris-reader {\"theme\":\"light\",\"size\":18,\"serif\":false}")]
    [InlineData("eteris-reader {\"theme\":\"neon\",\"size\":18,\"serif\":false}")]
    [InlineData("eteris-reader {\"theme\":\"dark\",\"size\":90,\"serif\":false}")]
    [InlineData("eteris-reader não é json")]
    [InlineData(null)]
    public void ParseReport_IgnoresAnythingElse(string? message) => Assert.Null(ReaderScripts.ParseReport(message));
}
