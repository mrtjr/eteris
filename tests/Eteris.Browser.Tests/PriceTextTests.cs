using System.Globalization;
using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// A vigia de preço lê o preço que a página mostra em texto: «R$ 1.299,90», «1 299,90 €», «$1,299.90» ou o número cru
// dos dados estruturados. Os espaços inseparáveis e finos e o símbolo colado ao número também se leem.
public class PriceTextTests
{
    [Theory]
    [InlineData("R$ 1.299,90", "1299.90")]
    [InlineData("1 299,90 €", "1299.90")]
    [InlineData("$1,299.90", "1299.90")]
    [InlineData("R$ 588,22", "588.22")]
    [InlineData("1234.56", "1234.56")]          // JSON-LD
    [InlineData("1349.9", "1349.9")]
    [InlineData("R$ 1.234", "1234")]            // ponto de milhares sem cêntimos
    [InlineData("12,5 €", "12.5")]
    public void Parse_ReadsCommonFormats(string text, string expected)
        => Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), PriceText.Parse(text));

    [Theory]
    [InlineData("R$ 1.299,90")]
    [InlineData("1 299,90 €")]
    [InlineData("R$1.299,90")]
    public void Parse_HandlesNbspAndGluedSymbols(string text) => Assert.Equal(1299.90m, PriceText.Parse(text));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("Esgotado")] [InlineData("12x sem juros")]
    public void Parse_ReturnsNullWithoutANumber(string? text) => Assert.Null(PriceText.Parse(text));

    // O espaço só separa milhares quando lhe seguem exatamente 3 algarismos: «588,22 em 12x» não junta o 12 ao preço.
    [Fact]
    public void Parse_StopsAtASpaceThatIsNotAThousandsSeparator()
        => Assert.Equal(588.22m, PriceText.Parse("R$ 588,22 em 12x"));

    [Fact]
    public void Format_UsesPortugueseSeparators()
    {
        Assert.Equal("R$ 1.299,90", PriceText.Format(1299.90m, "BRL"));
        Assert.Equal("1.299,90 €", PriceText.Format(1299.90m, "EUR"));
        Assert.Equal("1.299,90", PriceText.Format(1299.90m, null));
    }
}
