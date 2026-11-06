using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class LinkFormatsTests
{
    [Fact]
    public void Markdown_EscapesBracketsAndParentheses()
    {
        Assert.Equal("[Fado – Wikipédia](https://pt.wikipedia.org/wiki/Fado)", LinkFormats.Markdown("Fado –\n Wikipédia", "https://pt.wikipedia.org/wiki/Fado"));
        Assert.Equal(@"[\[Ao vivo\] Jogo](https://a.pt/x_%28y%29)", LinkFormats.Markdown("[Ao vivo] Jogo", "https://a.pt/x_(y)"));
        Assert.Equal("[https://a.pt/](https://a.pt/)", LinkFormats.Markdown("  ", "https://a.pt/"));
    }

    [Fact]
    public void TitleAndAddress_TwoLinesOrJustTheAddress()
    {
        Assert.Equal("PÚBLICO\nhttps://www.publico.pt/", LinkFormats.TitleAndAddress("PÚBLICO", "https://www.publico.pt/"));
        Assert.Equal("https://a.pt/", LinkFormats.TitleAndAddress("https://a.pt/", "https://a.pt/"));
    }
}
