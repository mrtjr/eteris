using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class InstantAnswerTests
{
    [Theory]
    [InlineData("2+2", "4")]
    [InlineData("2+2*3", "8")]
    [InlineData("(12+8)*1,5", "30")]
    [InlineData("(12+8)*1.5", "30")]
    [InlineData("10/4", "2,5")]
    [InlineData("2^10", "1024")]
    [InlineData("-3+5", "2")]
    [InlineData("50%", "0,5")]
    [InlineData("sqrt(16)+1", "5")]
    [InlineData("1/3", "0,333333333")]
    [InlineData("= 7 * 6", "42")]
    public void Calculate_EvaluatesArithmetic(string input, string expected)
        => Assert.Equal(expected, InstantAnswer.Calculate(input));

    [Theory]
    [InlineData("42")]
    [InlineData("github.com")]
    [InlineData("receita de bacalhau")]
    [InlineData("1/0")]
    [InlineData("2+")]
    [InlineData("(1+2")]
    [InlineData("3.14.15")]
    public void Calculate_NotAnExpression_IsNull(string input)
        => Assert.Null(InstantAnswer.Calculate(input));

    [Theory]
    [InlineData("10 km em milhas", "6,21 mi")]
    [InlineData("10km to mi", "6,21 mi")]
    [InlineData("30 °C em F", "86 °F")]
    [InlineData("86 f em celsius", "30 °C")]
    [InlineData("2 kg em lb", "4,41 lb")]
    [InlineData("12 polegadas em cm", "30,48 cm")]
    [InlineData("1,5 m em cm", "150 cm")]
    public void Convert_HandlesCommonUnits(string input, string expected)
        => Assert.Equal(expected, InstantAnswer.Convert(input));

    [Theory]
    [InlineData("10 km em kg")]
    [InlineData("km em mi")]
    [InlineData("10 parsecs em km")]
    public void Convert_IncompatibleOrUnknown_IsNull(string input)
        => Assert.Null(InstantAnswer.Convert(input));

    [Fact]
    public void Suggestions_AnswerComesFirstButSearchStaysPrimary()
    {
        var list = Suggestions.Build("2+2*3", SearchEngine.DuckDuckGo, []);
        Assert.Equal(SuggestionKind.Answer, list[0].Kind);
        Assert.Equal("= 8", list[0].Text);
        Assert.Equal(SuggestionKind.Search, list[1].Kind);
    }
}
