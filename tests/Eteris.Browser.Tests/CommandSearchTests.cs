using Eteris.Browser.Palette;

namespace Eteris.Browser.Tests;

public class CommandSearchTests
{
    [Theory]
    [InlineData("nova", "Nova aba")]
    [InlineData("na", "Nova aba")]
    [InlineData("NOVA", "Nova aba")]
    [InlineData("hist", "Histórico")]
    [InlineData("historico", "Limpar histórico")]
    [InlineData("acao", "Ação rápida")]
    [InlineData("rab", "Reabrir aba fechada")]
    public void Score_MatchesSubsequenceIgnoringCaseAndAccents(string query, string text)
        => Assert.True(CommandSearch.Score(query, text) > 0);

    [Theory]
    [InlineData("xyz", "Nova aba")]
    [InlineData("abaa", "Nova aba")]
    [InlineData("zoom", "Modo foco")]
    public void Score_NoMatchIsZero(string query, string text)
        => Assert.Equal(0, CommandSearch.Score(query, text));

    [Fact]
    public void Score_EmptyQueryMatchesEverything()
        => Assert.True(CommandSearch.Score("", "Qualquer coisa") > 0);

    [Fact]
    public void Score_PrefixBeatsMiddleOfWord()
        => Assert.True(CommandSearch.Score("fech", "Fechar aba") > CommandSearch.Score("fech", "Reabrir aba fechada"));

    [Fact]
    public void Score_WordStartsBeatScatteredLetters()
        => Assert.True(CommandSearch.Score("na", "Nova aba") > CommandSearch.Score("na", "Ecrã inteiro"));

    [Fact]
    public void Score_ConsecutiveBeatsSpread()
        => Assert.True(CommandSearch.Score("zoom", "Aumentar zoom") > CommandSearch.Score("zoom", "Zona de ocupação ou modo"));

    [Fact]
    public void Rank_OrdersByScoreAndDropsNonMatches()
    {
        var ranked = CommandSearch.Rank("aba", ["Modo foco", "Reabrir aba fechada", "Nova aba", "Fechar aba"], s => s);
        Assert.Equal(3, ranked.Count);
        Assert.DoesNotContain("Modo foco", ranked);
    }

    [Fact]
    public void RankTitled_PrefersTitleAndDropsScatteredSubtitleMatches()
    {
        var items = new[]
        {
            ("Recarregar página", "GitHub · Change is constant. GitHub keeps you ahead."),
            ("Nova aba", "Abrir a página inicial"),
            ("Bem-vindo(a) ao Steam", "Mudar para esta aba · store.steampowered.com"),
        };
        var ranked = CommandSearch.RankTitled("aba", items, i => i.Item1, i => i.Item2);
        Assert.Equal("Nova aba", ranked[0].Item1);
        Assert.DoesNotContain(ranked, i => i.Item1 == "Recarregar página");
    }

    [Fact]
    public void RankTitled_SubtitleCanStillFindAnItem()
    {
        var items = new[] { ("Bem-vindo(a) ao Steam", "Mudar para esta aba · store.steampowered.com") };
        Assert.Single(CommandSearch.RankTitled("steampowered", items, i => i.Item1, i => i.Item2));
    }

    [Fact]
    public void Rank_EmptyQueryKeepsOriginalOrder()
        => Assert.Equal(["b", "a", "c"], CommandSearch.Rank("", ["b", "a", "c"], s => s));
}
