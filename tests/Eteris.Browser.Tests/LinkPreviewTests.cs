using System.Text.Json;
using Eteris.Browser.Browser;
using Eteris.Browser.Shields;

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

    [Theory]
    [InlineData("https://a.pt/artigo", "https://a.pt/artigo")]
    [InlineData("http://a.pt/x?y=1 ", "http://a.pt/x?y=1")]
    public void IsReport_ReadsTheClickedLink(string link, string expected)
    {
        Assert.True(LinkPreview.IsReport(LinkPreview.ReportPrefix + link, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eteris-glance https://a.pt/")]
    [InlineData("\u0001eteris-glance ")]
    [InlineData("\u0001eteris-glance javascript:alert(1)")]
    [InlineData("\u0001eteris-glance mailto:a@a.pt")]
    [InlineData("\u0001eteris-click -https://a.pt/")]
    public void IsReport_IgnoresEverythingElse(string? message) => Assert.False(LinkPreview.IsReport(message, out _));

    [Fact]
    public void IsReport_WithoutNonce_IsIgnored()
    {
        Assert.False(LinkPreview.IsReport("\u0001eteris-glance https://a.pt/", out _));
        Assert.True(LinkPreview.IsReport(LinkPreview.ReportPrefix + "https://a.pt/", out var url));
        Assert.Equal("https://a.pt/", url);
        // Com o segredo, o endereço continua a ter de ser da web.
        Assert.False(LinkPreview.IsReport(LinkPreview.ReportPrefix + "javascript:alert(1)", out _));
        Assert.False(LinkPreview.IsReport(LinkPreview.ReportPrefix + "mailto:a@a.pt", out _));
        Assert.False(LinkPreview.IsReport(LinkPreview.ReportPrefix, out _));
        Assert.Contains(PopupPolicy.ActivationNonce, LinkPreview.AltClickScript);
        Assert.Contains("'use strict';", LinkPreview.AltClickScript);
        Assert.DoesNotContain(PopupPolicy.ActivationNonce, LinkPreview.Script);
    }

    [Fact]
    public void AltClickScriptFor_PutsTheSecretInTheStrictScript()
    {
        var script = LinkPreview.AltClickScriptFor("ABC123");
        Assert.Contains("'\\u0001eteris-glance ABC123 '", script); // escape de JavaScript, não o caráter
        Assert.DoesNotContain("__NONCE__", script);
        Assert.StartsWith("(() => {\n  'use strict';", script.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AltClickScript_OnlyReactsToAPlainAltClick()
    {
        Assert.Contains("e.altKey", LinkPreview.AltClickScript);
        Assert.Contains("e.preventDefault()", LinkPreview.AltClickScript);
        Assert.Contains("isTrusted", LinkPreview.AltClickScript);
        Assert.Contains("}, true);", LinkPreview.AltClickScript); // em captura: a página não o cala
    }

    [Theory]
    [InlineData("Escape", false, false, false, GlanceAction.Close)]
    [InlineData("Return", false, false, false, GlanceAction.Open)]
    [InlineData("Return", true, false, false, GlanceAction.OpenSideBySide)]
    [InlineData("Return", false, true, false, GlanceAction.ReadLater)]
    [InlineData("Return", false, true, true, GlanceAction.None)]
    [InlineData("Return", true, true, false, GlanceAction.None)]
    [InlineData("A", false, false, false, GlanceAction.None)]
    [InlineData("Tab", false, false, false, GlanceAction.None)]
    public void ActionFor_MapsTheCardKeys(string key, bool shift, bool ctrl, bool isPrivate, GlanceAction expected)
        => Assert.Equal(expected, LinkPreview.ActionFor(key, shift, ctrl, isPrivate));

    [Fact]
    public void Script_CarriesReadabilityAndReturnsJson()
    {
        Assert.Contains("Readability", LinkPreview.Script);
        Assert.Contains("JSON.stringify", LinkPreview.Script);
        Assert.StartsWith("(() => {", LinkPreview.Script);
    }

    // ---------- Proposta 1 «Espreitar a sério»: resumo limpo, título sem o site, nome curto do site ----------

    // As duas linhas da captura 16 (Wikipédia «Europa continental»), tal como estão: coordenadas da caixa de informação
    // e as ligações «editar» coladas ao texto.
    [Theory]
    [InlineData("48° 12′ 30″ N, 16° 22′ 23″ LFuncionamentoEstatuto")]
    [InlineData("território (d)porção continentaleditar - editar código-fonte - editar Wikidata")]
    [InlineData("Origem: Wikipédia, a enciclopédia livre.")]
    [InlineData("Curto demais.")]
    [InlineData("")]
    [InlineData(null)]
    public void IsProse_RejectsCoordinatesAndEditLinks(string? line) => Assert.False(LinkPreview.IsProse(line));

    [Theory]
    [InlineData("A Europa continental é a parte continental da Europa, excluindo as ilhas e os arquipélagos que a rodeiam.")]
    [InlineData("O fado é um género musical português, geralmente cantado por uma só pessoa, acompanhada à guitarra portuguesa.")]
    [InlineData("Em 2024, a cidade recebeu 7,5 milhões de turistas, mais 12 % do que no ano anterior, segundo o INE.")]
    [InlineData("The iPhone was announced in January 2007 and went on sale in the United States that June.")]
    // Sem pontuação final, mas comprido: um parágrafo que acaba numa citação ou num nome continua a ser prosa.
    [InlineData("Uma frase longa que não acaba em ponto final porque o autor a deixou assim mas que tem palavras e letras suficientes para ser lida como prosa")]
    // Chinês, sem espaços entre palavras: a regra das seis palavras não se aplica.
    [InlineData("欧洲大陆是欧洲的大陆部分，不包括周围的岛屿和群岛，在英国通常被称为大陆，与不列颠群岛相对，也常用于其他岛国。")]
    public void IsProse_AcceptsARealParagraph(string line) => Assert.True(LinkPreview.IsProse(line));

    [Theory]
    // Mais de uma palavra colada (minúscula seguida de maiúscula): texto de estrutura, não prosa.
    [InlineData("InícioHistória GeografiaClima e muitas outras secções do artigo que aparecem coladas aqui.")]
    // Só números e símbolos: uma tabela achatada.
    [InlineData("1990 — 23 456 · 1991 — 24 567 · 1992 — 25 678 · 1993 — 26 789 · total 99.")]
    public void IsProse_RejectsStructureText(string line) => Assert.False(LinkPreview.IsProse(line));

    [Fact]
    public void Parse_PrefersParasOverText()
    {
        var json = JsonSerializer.Serialize(new
        {
            head = "<head><meta property=\"og:title\" content=\"Europa continental – Wikipédia, a enciclopédia livre\"></head>",
            title = "Europa continental – Wikipédia, a enciclopédia livre",
            site = "Wikimedia Foundation, Inc.",
            text = "Origem: Wikipédia, a enciclopédia livre.\n48° 12′ 30″ N, 16° 22′ 23″ LFuncionamentoEstatuto\nterritório (d)porção continentaleditar - editar código-fonte - editar Wikidata\nTexto do textContent que não devia aparecer, porque há parágrafos limpos do artigo.",
            paras = new[]
            {
                "48° 12′ 30″ N, 16° 22′ 23″ LFuncionamentoEstatuto",
                "A Europa continental é a parte continental da Europa, excluindo as ilhas e os arquipélagos que a rodeiam.",
                "No Reino Unido, o termo «o Continente» designa muitas vezes a Europa continental, por oposição às ilhas britânicas.",
                "O conceito varia conforme o país: na Islândia e em Malta usa-se com um sentido parecido, mas não igual.",
                "Há ainda um quarto parágrafo de prosa, que fica para «Ler tudo aqui» e não aparece no resumo do cartão.",
            },
            words = 900,
        });
        var info = LinkPreview.Parse(json, "https://pt.wikipedia.org/wiki/Europa_continental")!;
        Assert.Equal("Europa continental", info.Title);
        Assert.Equal("Wikipédia", info.Site);
        Assert.Equal(3, info.Paragraphs.Count);
        Assert.StartsWith("A Europa continental é", info.Paragraphs[0]);
        Assert.DoesNotContain(info.Paragraphs, p => p.Contains("editar", StringComparison.Ordinal) || p.Contains('°'));
        Assert.DoesNotContain(info.Paragraphs, p => p.Contains("textContent", StringComparison.Ordinal));
        Assert.Equal(4, info.FullParagraphs.Count);
        Assert.True(info.HasMore);
    }

    [Fact]
    public void Parse_WithoutParas_FiltersTextLines()
    {
        var json = JsonSerializer.Serialize(new
        {
            head = "",
            text = "48° 12′ 30″ N, 16° 22′ 23″ LFuncionamentoEstatuto\nterritório (d)porção continentaleditar - editar código-fonte - editar Wikidata\nA Europa continental é a parte continental da Europa, excluindo as ilhas e os arquipélagos que a rodeiam.",
            words = 40,
        });
        var info = LinkPreview.Parse(json, "https://pt.wikipedia.org/wiki/Europa_continental")!;
        var only = Assert.Single(info.Paragraphs);
        Assert.StartsWith("A Europa continental é", only);
        Assert.False(info.HasMore);
    }

    [Fact]
    public void Parse_CjkWithoutSpaces_FallsBackToUnfilteredParas()
    {
        // Parágrafos sem pontuação final e com menos de 120 caracteres: nenhum passa no IsProse, e o cartão mostra os
        // primeiros de «paras» sem o filtro (como antes), em vez de ficar sem resumo.
        var paras = Enumerable.Range(1, 4).Select(i => $"第{i}段欧洲大陆是欧洲的大陆部分不包括周围的岛屿和群岛在英国通常被称为大陆与不列颠群岛相对这里没有句号").ToArray();
        Assert.All(paras, p => Assert.False(LinkPreview.IsProse(p)));
        var json = JsonSerializer.Serialize(new { head = "", text = "", paras, words = 0 });
        var info = LinkPreview.Parse(json, "https://zh.wikipedia.org/wiki/x")!;
        Assert.Equal(3, info.Paragraphs.Count);
        Assert.Equal(paras[0], info.Paragraphs[0]);
    }

    [Fact]
    public void Parse_ExcerptIsTheDescription_WhenThereIsNoMetaDescription()
    {
        const string excerpt = "Uma descrição do Readability, tirada do artigo, com palavras suficientes para ser prosa.";
        var json = JsonSerializer.Serialize(new
        {
            head = "",
            excerpt,
            paras = new[] { "O primeiro parágrafo do artigo é outro, com texto suficiente para contar como prosa." },
        });
        Assert.Equal(excerpt, LinkPreview.Parse(json, Page)!.Description);
        // Um excerto igual ao 1.º parágrafo não se repete; um que não é prosa não entra.
        var same = JsonSerializer.Serialize(new { head = "", excerpt, paras = new[] { excerpt } });
        Assert.Null(LinkPreview.Parse(same, Page)!.Description);
        var junk = JsonSerializer.Serialize(new { head = "", excerpt = "48° 12′ 30″ N, 16° 22′ 23″ LFuncionamentoEstatuto", paras = new[] { excerpt } });
        Assert.Null(LinkPreview.Parse(junk, Page)!.Description);
    }

    [Fact]
    public void Parse_FullParagraphs_KeepTheWholeTextUpToTheBudget()
    {
        var long1 = string.Join(' ', Enumerable.Repeat("palavra", 150)) + ".";
        var paras = Enumerable.Range(0, 30).Select(_ => long1).ToArray();
        var info = LinkPreview.Parse(JsonSerializer.Serialize(new { head = "", paras }), Page)!;
        // O resumo corta o último parágrafo numa palavra; o artigo inteiro guarda-o inteiro.
        Assert.EndsWith("…", info.Paragraphs[^1]);
        Assert.Equal(long1, info.FullParagraphs[0]);
        Assert.True(info.FullParagraphs.Sum(p => p.Length) <= LinkPreview.MaxFullChars);
        Assert.True(info.FullParagraphs.Count > 3);
        Assert.True(info.HasMore);
    }

    [Fact]
    public void CleanTitle_StripsWikipediaSuffix()
    {
        var (title, tail) = LinkPreview.CleanTitle("Europa continental – Wikipédia, a enciclopédia livre", "Wikimedia Foundation, Inc.", "pt.wikipedia.org");
        Assert.Equal("Europa continental", title);
        Assert.Equal("Wikipédia, a enciclopédia livre", tail);
    }

    [Theory]
    [InlineData("PÚBLICO | Título", null, "www.publico.pt", "Título", "PÚBLICO")]
    [InlineData("Título da notícia - Observador", null, "observador.pt", "Título da notícia", "Observador")]
    [InlineData("Ukraine war latest - BBC News", "BBC News", "www.bbc.co.uk", "Ukraine war latest", "BBC News")]
    [InlineData("Como fiz isto – Blog do Zé", null, "blogdoze.pt", "Como fiz isto", "Blog do Zé")]
    // As duas pontas falam do site: sai a mais curta (o nome), não a descrição do repositório.
    [InlineData("GitHub - cli/cli: GitHub’s official command line tool", "GitHub", "github.com", "cli/cli: GitHub’s official command line tool", "GitHub")]
    public void CleanTitle_StripsLeadingBrand(string raw, string? site, string host, string expected, string expectedTail)
    {
        var (title, tail) = LinkPreview.CleanTitle(raw, site, host);
        Assert.Equal(expected, title);
        Assert.Equal(expectedTail, tail);
    }

    [Theory]
    [InlineData("Fado & saudade", "Exemplo", "exemplo.pt")]
    [InlineData("Economia – o que muda em 2027", null, "www.bbc.co.uk")]
    [InlineData("Spider-Man: Homecoming", null, "imdb.com")]
    [InlineData("Público", null, "www.publico.pt")]
    [InlineData("", null, "exemplo.pt")]
    public void CleanTitle_KeepsTitlesWithoutSite(string raw, string? site, string host)
    {
        var (title, tail) = LinkPreview.CleanTitle(raw, site, host);
        Assert.Equal(raw, title);
        Assert.Null(tail);
    }

    [Fact]
    public void ShortSite_PrefersOgSiteName_ThenTitleTail_IgnoresLegalEntity()
    {
        Assert.Equal("Exemplo", LinkPreview.ShortSite("Exemplo", "Wikipédia, a enciclopédia livre", "Wikimedia Foundation, Inc.", "pt.wikipedia.org"));
        Assert.Equal("Wikipédia", LinkPreview.ShortSite(null, "Wikipédia, a enciclopédia livre", "Wikimedia Foundation, Inc.", "pt.wikipedia.org"));
        // Um og:site_name comprido demais passa a vez.
        Assert.Equal("Wikipédia", LinkPreview.ShortSite("Um nome de site comprido demais para o cartão", "Wikipédia, a enciclopédia livre", null, "pt.wikipedia.org"));
        Assert.Equal("pt.wikipedia.org", LinkPreview.ShortSite(null, null, "Wikimedia Foundation, Inc.", "pt.wikipedia.org"));
        Assert.Equal("exemplo.pt", LinkPreview.ShortSite(null, null, "Exemplo, S.A.", "www.exemplo.pt"));
        Assert.Equal("exemplo.pt", LinkPreview.ShortSite(null, null, "Exemplo GmbH", "www.exemplo.pt"));
        Assert.Equal("O Blog", LinkPreview.ShortSite(null, null, "O Blog", "www.exemplo.pt"));
        Assert.Equal("exemplo.pt", LinkPreview.ShortSite(" ", " ", "", "www.exemplo.pt"));
    }

    [Theory]
    [InlineData("E", false, false, GlanceAction.Expand)]
    [InlineData("E", true, false, GlanceAction.None)]
    [InlineData("E", false, true, GlanceAction.None)]
    public void ActionFor_E_Expands(string key, bool shift, bool ctrl, GlanceAction expected)
        => Assert.Equal(expected, LinkPreview.ActionFor(key, shift, ctrl, isPrivate: false));

    [Fact]
    public void Script_KeepsTheArticleElement_AndCollectsCleanParagraphs()
    {
        var script = LinkPreview.Script;
        Assert.Contains("serializer: el => el", script);
        // O Readability tira as classes ao conteúdo; sem estas, .mw-editsection, .infobox e .navbox nunca se encontravam.
        Assert.Contains("classesToPreserve: ['mw-editsection', 'infobox', 'navbox']", script);
        Assert.Contains("const collectParas = ", script);
        Assert.Contains("paras = collectParas(", script);
        Assert.Contains("paras: paras,", script);
        Assert.Contains("excerpt: ", script);
        Assert.DoesNotContain("__COLLECT_PARAS__", script);
    }
}
