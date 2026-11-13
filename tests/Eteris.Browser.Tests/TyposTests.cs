using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// «Queria dizer X?» da página de erro: o site conhecido mais perto do nome que não resolve, a 2 edições ou menos, sem rede.
public class TyposTests
{
    [Theory]
    [InlineData("publico.ptt", "publico.pt")]          // uma letra a mais
    [InlineData("pubilco.pt", "publico.pt")]           // duas letras trocadas (2 edições)
    [InlineData("www.gihtub.com", "github.com")]       // o «www.» não conta
    [InlineData("GITHUB.CON", "github.com")]           // sem diferença de maiúsculas
    public void Closest_FindsTheKnownSiteWithinTwoEdits(string host, string expected)
        => Assert.Equal(expected, Typos.Closest(host, ["exemplo.pt", "github.com", "publico.pt"]));

    [Theory]
    [InlineData("publico.pt")]          // o próprio nome não é sugestão
    [InlineData("www.publico.pt")]
    [InlineData("jornal.pt")]           // longe de todos
    [InlineData("")]
    public void Closest_SuggestsNothing_WhenNoneIsCloseOrItIsTheSame(string host)
        => Assert.Null(Typos.Closest(host, ["publico.pt", "github.com"]));

    // O mais perto ganha; num empate, o primeiro da lista (o histórico vem ordenado pelas visitas).
    [Fact]
    public void Closest_PrefersTheNearest_ThenTheFirst()
    {
        Assert.Equal("abcd.pt", Typos.Closest("abcx.pt", ["abxy.pt", "abcd.pt"]));
        Assert.Equal("abce.pt", Typos.Closest("abcx.pt", ["abce.pt", "abcd.pt"]));
        Assert.Null(Typos.Closest("publico.ptt", []));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("publico", "pubilco", 2)]
    public void Distance_IsTheEditDistance(string a, string b, int distance)
    {
        Assert.Equal(distance, Typos.Distance(a, b));
        Assert.Equal(distance, Typos.Distance(b, a));
    }
}
