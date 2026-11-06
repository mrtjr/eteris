using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class LinkStatusTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("https://pt.wikipedia.org/wiki/S%C3%A3o_Paulo", "https://pt.wikipedia.org/wiki/São_Paulo")]
    [InlineData("https://a.pt/a%20b", "https://a.pt/a b")]
    public void Format_IsReadable(string? input, string? expected)
        => Assert.Equal(expected, LinkStatus.Format(input));

    [Fact]
    public void Format_ShortensVeryLongLinks()
    {
        var text = LinkStatus.Format("https://a.pt/" + new string('x', 400))!;
        Assert.Equal(LinkStatus.MaxLength, text.Length);
        Assert.EndsWith("…", text);
    }
}
