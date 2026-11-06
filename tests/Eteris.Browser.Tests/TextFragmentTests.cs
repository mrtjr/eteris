using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class TextFragmentTests
{
    [Fact]
    public void ShortSelection_IsQuotedWhole()
        => Assert.Equal("https://pt.wikipedia.org/wiki/Fado#:~:text=estilo%20musical%20portugu%C3%AAs",
            TextFragment.Link("https://pt.wikipedia.org/wiki/Fado#Origem", "  estilo   musical\nportuguês "));

    [Fact]
    public void LongSelection_UsesStartAndEnd()
    {
        var link = TextFragment.Link("https://a.pt/x", "um dois três quatro cinco seis sete oito nove dez onze doze");
        Assert.Equal("https://a.pt/x#:~:text=um%20dois%20tr%C3%AAs%20quatro,nove%20dez%20onze%20doze", link);
    }

    [Fact]
    public void ReservedCharactersAreEncodedAndTrackersRemoved()
    {
        var link = TextFragment.Link("https://a.pt/x?id=7&utm_source=news", "pré-venda, já & agora");
        Assert.Equal("https://a.pt/x?id=7#:~:text=pr%C3%A9%2Dvenda%2C%20j%C3%A1%20%26%20agora", link);
    }

    [Theory]
    [InlineData("eteris://settings/", "texto")]
    [InlineData("https://a.pt/", "   ")]
    public void NothingToLink(string url, string selection) => Assert.Null(TextFragment.Link(url, selection));

    [Fact]
    public void MenuOffersItOnlyForSelectionsOnWebPages()
    {
        var withSelection = PageMenu.Build(new PageContext(null, null, "olá mundo", false, false, false, false), "DuckDuckGo");
        Assert.Contains(withSelection, i => i?.Command == PageCommand.CopyHighlightLink);
        var editable = PageMenu.Build(new PageContext(null, null, "olá", true, false, false, false), "DuckDuckGo");
        Assert.DoesNotContain(editable, i => i?.Command == PageCommand.CopyHighlightLink);
        var internalPage = PageMenu.Build(new PageContext(null, null, "olá", false, false, false, false, IsWebPage: false), "DuckDuckGo");
        Assert.DoesNotContain(internalPage, i => i?.Command == PageCommand.CopyHighlightLink);
    }
}
