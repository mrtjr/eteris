using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class OmniboxMenuTests
{
    [Theory]
    [InlineData("https://www.publico.pt/artigo", "Colar e ir para www.publico.pt")]
    [InlineData("  github.com/mrtjr/eteris \n", "Colar e ir para github.com")]
    [InlineData("receita de bacalhau à brás", "Colar e pesquisar «receita de bacalhau à brás»")]
    [InlineData("uma frase muito comprida que não cabe no menu", "Colar e pesquisar «uma frase muito comprida…»")]
    public void PasteAndGoLabel_SaysWhereItGoes(string clipboard, string expected)
        => Assert.Equal(expected, MainWindow.PasteAndGoLabel(clipboard, SearchEngine.DuckDuckGo));
}
