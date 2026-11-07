using System.Text.Json;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class LinkPreviewTests
{
    private const string Page = "https://www.exemplo.pt/noticias/artigo?id=3";

    private const string Html = """
        <!doctype html>
        <html lang="pt">
        <head>
          <meta charset="utf-8">
          <title>Fado &amp; saudade – Exemplo, o jornal</title>
          <meta name="description" content="Descrição simples da página.">
          <meta property="og:title" content="Fado &amp; saudade">
          <meta property='og:description' content='O fado nasceu em Lisboa e &eacute; hoje patrim&oacute;nio da humanidade.'>
          <meta content="/imagens/fado.jpg" property="og:image">
          <meta property="og:image" content="https://cdn.exemplo.pt/outra.jpg">
          <meta property="og:site_name" content="Exemplo">
          <link rel="image_src" href="https://cdn.exemplo.pt/antiga.jpg">
        </head>
        <body>
          <p class="x">Curto.</p>
          <p>O fado é um <b>género musical</b> português, geralmente cantado por uma só pessoa, acompanhada à guitarra portuguesa.</p>
          <p>As suas origens remontam ao início do século XIX, nos bairros populares de Lisboa, como Alfama e a Mouraria.</p>
        </body>
        </html>
        """;

    [Fact]
    public void FromHtml_PrefersOpenGraphAndDecodesEntities()
    {
        var meta = LinkPreview.FromHtml(Html, Page);
        Assert.Equal("Fado & saudade", meta.Title);
        Assert.Equal("O fado nasceu em Lisboa e é hoje património da humanidade.", meta.Description);
        Assert.Equal("Exemplo", meta.SiteName);
    }

    [Fact]
    public void FromHtml_ResolvesRelativeImageAgainstThePage_FirstTagWins()
        => Assert.Equal("https://www.exemplo.pt/imagens/fado.jpg", LinkPreview.FromHtml(Html, Page).ImageUrl);

    [Fact]
    public void FromHtml_FallsBackToTitleTagDescriptionAndLinkImage()
    {
        const string plain = """
            <head><title> Só o título </title><meta name="description" content="Meta description."><link href="//cdn.exemplo.pt/i.png" rel="image_src"></head>
            """;
        var meta = LinkPreview.FromHtml(plain, Page);
        Assert.Equal("Só o título", meta.Title);
        Assert.Equal("Meta description.", meta.Description);
        Assert.Equal("https://cdn.exemplo.pt/i.png", meta.ImageUrl);
        Assert.Null(meta.SiteName);
    }

    [Fact]
    public void FromHtml_CollectsReadableParagraphsWithoutTags()
    {
        var paragraphs = LinkPreview.FromHtml(Html, Page).Paragraphs;
        Assert.Equal(2, paragraphs.Count);
        Assert.StartsWith("O fado é um género musical português", paragraphs[0]);
        Assert.DoesNotContain("<b>", paragraphs[0]);
    }

    [Fact]
    public void FromHtml_EmptyOrGarbage_GivesNothing()
    {
        var meta = LinkPreview.FromHtml("<meta content=x><meta name=>", Page);
        Assert.Null(meta.Title);
        Assert.Null(meta.Description);
        Assert.Null(meta.ImageUrl);
        Assert.Empty(meta.Paragraphs);
        Assert.Empty(LinkPreview.FromHtml(null, Page).Paragraphs);
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("  https://a.pt/x.png  ", "https://a.pt/x.png")]
    [InlineData("../i.png", "https://www.exemplo.pt/i.png")]
    [InlineData("", null)]
    public void AbsoluteUrl_OnlyWebAddresses(string candidate, string? expected)
        => Assert.Equal(expected, LinkPreview.AbsoluteUrl(candidate, Page));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(229, 1)]
    [InlineData(460, 2)]
    [InlineData(2300, 10)]
    public void ReadingMinutes_RoundsToAtLeastOneMinute(int words, int minutes)
        => Assert.Equal(minutes, LinkPreview.ReadingMinutes(words));

    [Fact]
    public void Paragraphs_SkipShortLinesAndStopAtThree()
    {
        var text = "Título\n" + string.Join('\n', Enumerable.Range(1, 5).Select(i => $"Parágrafo número {i} com texto suficiente para contar como parágrafo legível."));
        var paragraphs = LinkPreview.Paragraphs(text);
        Assert.Equal(3, paragraphs.Count);
        Assert.StartsWith("Parágrafo número 1", paragraphs[0]);
    }

    [Fact]
    public void Paragraphs_RespectTheCharacterBudgetWithEllipsisOnAWord()
    {
        var text = string.Join(' ', Enumerable.Repeat("palavra", 200));
        var paragraphs = LinkPreview.Paragraphs(text, maxChars: 100);
        var only = Assert.Single(paragraphs);
        Assert.EndsWith("palavra…", only);
        Assert.True(only.Length <= 101);
    }

    [Fact]
    public void Parse_CombinesHeadMetadataWithTheReadabilityText()
    {
        var json = JsonSerializer.Serialize(new
        {
            head = "<head><title>Ignorado</title><meta property=\"og:title\" content=\"Do Open Graph\"><meta property=\"og:image\" content=\"/i.jpg\"></head>",
            title = "Do Readability",
            byline = " Maria  Silva ",
            site = "",
            text = "Primeiro parágrafo do artigo, com texto suficiente para contar como legível.\nSegundo parágrafo também bastante comprido para passar o limite mínimo.",
            words = 460,
        });
        var info = LinkPreview.Parse(json, Page)!;
        Assert.Equal("Do Open Graph", info.Title);
        Assert.Equal("exemplo.pt", info.Site);
        Assert.Equal("https://www.exemplo.pt/i.jpg", info.ImageUrl);
        Assert.Equal("Maria Silva", info.Byline);
        Assert.Equal(2, info.Paragraphs.Count);
        Assert.Equal(2, info.ReadingMinutes);
        Assert.Equal("2 min de leitura", info.ReadingTime);
    }

    [Fact]
    public void Parse_WithoutMetadata_FallsBackToHostAndCountsWords()
    {
        var json = JsonSerializer.Serialize(new { head = "", title = "", text = "Uma linha de texto com palavras suficientes para contar como parágrafo aqui.", words = 0 });
        var info = LinkPreview.Parse(json, "https://blog.exemplo.pt/x")!;
        Assert.Equal("blog.exemplo.pt", info.Title);
        Assert.Equal("blog.exemplo.pt", info.Site);
        Assert.Null(info.ImageUrl);
        Assert.Equal(12, info.Words);
        Assert.Equal("1 min de leitura", info.ReadingTime);
    }

    [Fact]
    public void Parse_DropsADescriptionThatRepeatsTheFirstParagraph()
    {
        var first = "O fado é um género musical português cantado por uma só pessoa.";
        var json = JsonSerializer.Serialize(new
        {
            head = $"<meta name=\"description\" content=\"{first[..40]}…\">",
            text = first + "\nOutro parágrafo diferente, com texto suficiente para ser mostrado no cartão.",
            words = 30,
        });
        var info = LinkPreview.Parse(json, Page)!;
        Assert.Null(info.Description);
        Assert.Equal(2, info.Paragraphs.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("[1,2]")]
    public void Parse_RejectsGarbage(string? json) => Assert.Null(LinkPreview.Parse(json, Page));

    [Fact]
    public void Script_CarriesReadabilityAndReturnsJson()
    {
        Assert.Contains("Readability", LinkPreview.Script);
        Assert.Contains("JSON.stringify", LinkPreview.Script);
        Assert.StartsWith("(() => {", LinkPreview.Script);
    }
}
