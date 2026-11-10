using Eteris.Sync.Pairing;

namespace Eteris.Browser.Tests.Sync;

// O código que o computador antigo pede (0.2.15, T8): o que a pessoa escreve compara-se com o código de verificação
// calculado, sem nunca o mostrar. Só algarismos ASCII, seis, com espaços, hífenes e pontos à vontade.
public class PairingCodeTests
{
    [Theory]
    [InlineData("482 913")]
    [InlineData("482913")]
    [InlineData(" 482-913 ")]
    [InlineData("482\u00A0913")]
    [InlineData("48.29.13")]
    public void Normalize_AcceptsDigitsWithSpacesAndHyphens(string typed)
        => Assert.Equal("482913", PairingCode.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("48291")]
    [InlineData("4829135")]
    [InlineData("48291a")]
    [InlineData("abc def")]
    [InlineData("\u0664\u0668\u0662\u0669\u0661\u0663")]   // algarismos árabes
    [InlineData("\uFF14\uFF18\uFF12\uFF19\uFF11\uFF13")]   // algarismos de largura total
    public void Normalize_RejectsAnythingElse(string? typed)
        => Assert.Null(PairingCode.Normalize(typed));

    [Fact]
    public void Matches_OnlyTheSameSixDigits()
    {
        Assert.True(PairingCode.Matches("482 913", "482 913"));
        Assert.True(PairingCode.Matches("482 913", "482913"));
        Assert.False(PairingCode.Matches("482 913", "482 914"));
        Assert.False(PairingCode.Matches("482 913", "482 91"));
        Assert.False(PairingCode.Matches("482 913", null));
        Assert.False(PairingCode.Matches("482 913", ""));
        Assert.Equal(6, PairingCode.Digits);
        Assert.Equal(3, PairingCode.MaxWrong);
    }
}
