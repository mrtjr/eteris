using System.Text.Json;
using Eteris.Browser.Shields;
using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// A resposta do script de leitura (WatchScripts.Read) passa a PageReading: o preço estruturado à frente do pedaço, e
// uma resposta estragada é uma leitura vazia (a página não carregou), nunca uma exceção.
public class WatchReadingParserTests
{
    [Fact]
    public void Parse_PrefersStructuredPriceOverInstallments()
    {
        var json = """{"ld":["{\"@type\":\"Product\",\"offers\":{\"price\":\"588.22\",\"priceCurrency\":\"BRL\"}}"],"itemprop":[],"meta":[],"currency":null,"sel":"12x de R$ 49,02","selFound":true,"title":"SSD","challenge":false}""";
        var r = WatchReadingParser.Parse(json, "https://www.pichau.com.br/ssd");
        Assert.Equal(588.22m, r.Price!.Value);
        Assert.Equal("ld", r.Price.Source);
    }

    [Fact]
    public void Parse_UsesPickedPieceWhenNoStructuredPrice()
    {
        var r = WatchReadingParser.Parse("""{"ld":[],"itemprop":[],"meta":[],"currency":null,"sel":"R$ 1.234,56","selFound":true,"title":"Loja","challenge":false}""", "https://loja.pt/p");
        Assert.Equal(new PriceReading(1234.56m, "BRL", "texto"), r.Price);
        Assert.Equal("R$ 1.234,56", r.PieceText);
    }

    [Theory]
    [InlineData("ERR")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("""{"ld":"x","itemprop":5,"meta":null,"currency":3,"sel":{},"selFound":"sim","title":7,"challenge":"x"}""")]
    [InlineData("""{"ld":[1,null,{}],"itemprop":[2],"meta":[true],"title":"","selFound":true}""")]
    public void Parse_GarbageIsAnEmptyReading(string json)
    {
        var r = WatchReadingParser.Parse(json, "https://a.pt/");
        Assert.Null(r.Price); Assert.False(r.PieceFound);
        Assert.Null(r.PieceText);
        Assert.Equal("", r.Title);
        Assert.Equal("https://a.pt/", r.FinalUrl);
        Assert.False(r.ChallengeMarker);
    }

    [Fact]
    public void Parse_KeepsTitleUrlAndPiece()
    {
        var r = WatchReadingParser.Parse("""{"ld":[],"itemprop":[],"meta":[],"currency":null,"sel":"  Esgotado \n","selFound":true,"title":"Ténis","challenge":false}""", "https://loja.pt/t");
        Assert.Null(r.Price);
        Assert.True(r.PieceFound);
        Assert.Equal("  Esgotado \n", r.PieceText);   // em bruto: o WatchRules.Normalize trata dos espaços
        Assert.Equal("Ténis", r.Title);
        Assert.Equal("https://loja.pt/t", r.FinalUrl);
    }

    [Fact]
    public void Parse_PieceNotFound_HasNoText()
    {
        var r = WatchReadingParser.Parse("""{"ld":[],"itemprop":[],"meta":[],"currency":null,"sel":null,"selFound":false,"title":"Loja","challenge":false}""", "https://loja.pt/p");
        Assert.False(r.PieceFound);
        Assert.Null(r.PieceText);
        Assert.Null(r.Price);
    }

    // A moeda que a página declara (priceCurrency) vale para o itemprop e para o pedaço.
    [Fact]
    public void Parse_PageCurrencyIsTheHint()
    {
        var r = WatchReadingParser.Parse("""{"ld":[],"itemprop":["907.99"],"meta":[],"currency":"EUR","sel":null,"selFound":false,"title":"Loja","challenge":false}""", "https://loja.pt/p");
        Assert.Equal(new PriceReading(907.99m, "EUR", "itemprop"), r.Price);
    }

    // A página de desafio (#challenge-form, #cf-challenge-running) é sempre um desafio, mesmo que o pedaço apontado
    // («h1») também exista nela: nunca dá «mudou».
    [Fact]
    public void Parse_ChallengePage_IsAlwaysAMarker()
    {
        var bare = WatchReadingParser.Parse("""{"ld":[],"itemprop":[],"meta":[],"currency":null,"sel":null,"selFound":false,"title":"Just a moment...","challenge":true,"captcha":false}""", "https://loja.pt/p");
        Assert.True(bare.ChallengeMarker);
        var withPiece = WatchReadingParser.Parse("""{"ld":[],"itemprop":[],"meta":[],"currency":null,"sel":"loja.pt","selFound":true,"title":"Just a moment...","challenge":true,"captcha":false}""", "https://loja.pt/p");
        Assert.True(withPiece.ChallengeMarker);
    }

    // Um CAPTCHA sem preço nem pedaço é um desafio; num formulário (newsletter, avaliações) de uma página que se leu não
    // é: a vigia não pode ficar «bloqueada» por isso.
    [Fact]
    public void Parse_CaptchaCountsOnlyWhenNothingWasRead()
    {
        var nothing = WatchReadingParser.Parse("""{"ld":[],"itemprop":[],"meta":[],"currency":null,"sel":null,"selFound":false,"title":"Verificação","challenge":false,"captcha":true}""", "https://loja.pt/p");
        Assert.True(nothing.ChallengeMarker);
        var withPrice = WatchReadingParser.Parse("""{"ld":["{\"@type\":\"Product\",\"offers\":{\"price\":\"10\"}}"],"itemprop":[],"meta":[],"currency":null,"sel":null,"selFound":false,"title":"Loja","challenge":false,"captcha":true}""", "https://loja.pt/p");
        Assert.False(withPrice.ChallengeMarker);
        var withPiece = WatchReadingParser.Parse("""{"ld":[],"itemprop":[],"meta":[],"currency":null,"sel":"Novidades","selFound":true,"title":"Loja","challenge":false,"captcha":true}""", "https://loja.pt/p");
        Assert.False(withPiece.ChallengeMarker);
    }

    // O script de leitura e o parser falam o mesmo JSON: o script corre no Node com um document de faz-de-conta (dados
    // estruturados, o pedaço «#preco» e um reCAPTCHA de newsletter) e a resposta passa pelo parser.
    [Fact]
    public void Read_ProducesWhatTheParserReads()
    {
        var stub = """
            const ld = { textContent: '{"@type":"Product","offers":{"price":"588.22"}}' };
            const price = { hasAttribute: n => n === 'content', getAttribute: () => '588.22', textContent: 'R$ 588,22' };
            const currency = { hasAttribute: n => n === 'content', getAttribute: () => 'brl', textContent: '' };
            globalThis.document = {
              title: 'SSD 1 TB',
              querySelectorAll: q => q.includes('ld+json') ? [ld] : q.includes('itemprop="price"') ? [price] : [],
              querySelector: q => q === '#preco' ? { innerText: '12x de R$ 49,02' }
                : q.includes('priceCurrency') ? currency
                : q.includes('g-recaptcha') ? {} : null,
            };
            console.log(
            """ + WatchScripts.Read("#preco") + ");";
        var reading = WatchReadingParser.Parse(NodeRunner.Run(stub).Trim(), "https://loja.pt/ssd");

        Assert.Equal(new PriceReading(588.22m, "BRL", "ld"), reading.Price);
        Assert.True(reading.PieceFound);
        Assert.Equal("12x de R$ 49,02", reading.PieceText);
        Assert.Equal("SSD 1 TB", reading.Title);
        Assert.False(reading.ChallengeMarker);
    }

    // O seletor de pedaços é um só: o «Esconder elemento» e a escolha do pedaço da vigia usam o mesmo construtor.
    [Fact]
    public void BothPickersShareTheSelectorBuilder()
    {
        Assert.Contains("function stableSelector(el)", PickerScript.SelectorBuilder);
        Assert.Contains(PickerScript.SelectorBuilder, PickerScript.Pick);
        Assert.Contains(PickerScript.SelectorBuilder, WatchScripts.PickPiece("Clique no pedaço a vigiar"));
    }

    // O seletor e a dica entram no script como texto JSON, nunca colados.
    [Fact]
    public void Scripts_EmbedSelectorAndTipAsJson()
    {
        const string selector = "a[title=\"x'y\"] </script>";
        Assert.Contains(JsonSerializer.Serialize(selector), WatchScripts.Read(selector));
        Assert.DoesNotContain(selector, WatchScripts.Read(selector));
        Assert.Contains("const selector = null;", WatchScripts.Read(null));
        Assert.Contains(JsonSerializer.Serialize("Clique «aqui» · Esc"), WatchScripts.PickPiece("Clique «aqui» · Esc"));
    }
}
