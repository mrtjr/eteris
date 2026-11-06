using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabFilterTests
{
    [Theory]
    [InlineData("Fado – Wikipédia", "https://pt.wikipedia.org/wiki/Fado", "wikipedia", true)]
    [InlineData("Fado – Wikipédia", "https://pt.wikipedia.org/wiki/Fado", "FADO wiki", true)]
    [InlineData("Previsão do tempo", "https://www.ipma.pt/", "previsao", true)]
    [InlineData("Previsão do tempo", "https://www.ipma.pt/", "ipma", true)]
    [InlineData("Previsão do tempo", "https://www.ipma.pt/", "tempo lisboa", false)]
    [InlineData("GitHub", "https://github.com/", "gitlab", false)]
    public void Matches_AllWordsInTitleOrAddressIgnoringAccents(string title, string address, string query, bool expected)
        => Assert.Equal(expected, TabFilter.Matches(title, address, query));
}
