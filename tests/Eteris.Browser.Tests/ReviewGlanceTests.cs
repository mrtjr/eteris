using System.IO;
using System.Text.Json;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// Correções da revisão do bloco A no «Espreitar»: corrida entre dois pedidos, Alt+clique nas páginas internas, nos
// sites fixados e em iframes, links para ficheiros, descrições gigantes e rolagem pelo teclado.
public class ReviewGlanceTests
{
    [Fact]
    public void TwoRequests_TheFirstDocumentFinishingLate_IsNotShownUnderTheSecond()
    {
        var loads = new GlanceLoads();
        var serial = 1;                     // Alt+clique em A
        loads.Committed(serial);            // A confirmado
        serial = 2;                         // Alt+clique em B antes de A acabar
        var finishedA = loads.Finished();   // o fim de A chega depois
        Assert.Equal(1, finishedA);
        Assert.NotEqual(serial, finishedA);
        loads.Committed(serial);            // B confirmado (também depois de redirecionamentos)
        Assert.Equal(2, loads.Finished());
    }

    [Fact]
    public void NoCommittedDocument_FinishesNothing() => Assert.Null(new GlanceLoads().Finished());

    [Theory]
    [InlineData("eteris://newtab", true)]
    [InlineData("eteris://bookmarks", true)]
    [InlineData("https://exemplo.pt/", true)]
    [InlineData("http://exemplo.pt/", true)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void AltClickScript_AlsoOnInternalPages(string? url, bool expected) => Assert.Equal(expected, LinkPreview.WantsAltClick(url));

    [Fact]
    public void AltClickDownload_IsDivertedOnlyWithAltAndAWebLink()
    {
        Assert.True(LinkPreview.DivertsDownload(true, "https://exemplo.pt/artigo"));
        Assert.False(LinkPreview.DivertsDownload(false, "https://exemplo.pt/artigo"));
        Assert.False(LinkPreview.DivertsDownload(true, "blob:https://exemplo.pt/1"));
        Assert.False(LinkPreview.DivertsDownload(true, null));
    }

    [Fact]
    public void FileStatus_SaysItIsAFile() => Assert.Contains("ficheiro", LinkPreview.FileStatus);

    [Fact]
    public void HugeDescription_IsCutOnAWord()
    {
        var article = string.Join(' ', Enumerable.Repeat("palavra", 400));
        var head = $"<head><meta property=\"og:description\" content=\"{article}\"><meta property=\"og:title\" content=\"Título\"></head>";
        var json = JsonSerializer.Serialize(new { head, title = "", byline = "", site = "", text = "", words = 0 });
        var info = LinkPreview.Parse(json, "https://exemplo.pt/a")!;
        Assert.NotNull(info.Description);
        Assert.True(info.Description!.Length <= LinkPreview.MaxDescriptionChars + 1);
        Assert.EndsWith("palavra…", info.Description);
        // Uma descrição curta fica inteira.
        var shortJson = JsonSerializer.Serialize(new { head = "<meta name=\"description\" content=\"Curta.\">", text = "" });
        Assert.Equal("Curta.", LinkPreview.Parse(shortJson, "https://exemplo.pt/a")!.Description);
    }

    [Theory]
    [InlineData("Down", false, GlanceScroll.LineDown)]
    [InlineData("Up", false, GlanceScroll.LineUp)]
    [InlineData("PageDown", false, GlanceScroll.PageDown)]
    [InlineData("Next", false, GlanceScroll.PageDown)]
    [InlineData("PageUp", false, GlanceScroll.PageUp)]
    [InlineData("Space", false, GlanceScroll.PageDown)]
    [InlineData("Space", true, GlanceScroll.PageUp)]
    [InlineData("Home", false, GlanceScroll.Top)]
    [InlineData("End", false, GlanceScroll.Bottom)]
    [InlineData("A", false, GlanceScroll.None)]
    [InlineData("Escape", false, GlanceScroll.None)]
    public void Keyboard_ScrollsTheCard(string key, bool shift, GlanceScroll expected) => Assert.Equal(expected, LinkPreview.ScrollFor(key, shift));

    // Proposta 1 «Espreitar a sério»: collectParas tira do conteúdo do Readability a caixa de informação, as ligações
    // «editar» e as notas antes de ler os parágrafos. O Node não tem DOM: a árvore é falsa (querySelectorAll, remove e
    // textContent), e um parágrafo dentro de uma tabela retirada não volta. A combinação com o Readability verdadeiro só
    // se prova ao vivo.
    [Fact]
    public void CollectParas_DropsInfoboxAndEditLinks()
    {
        const string fakeDom = """
            class N {
              constructor(tag, cls, text, kids) {
                this.tag = tag; this.cls = cls || ''; this.text = text || ''; this.kids = kids || []; this.parent = null;
                for (const k of this.kids) k.parent = this;
              }
              get textContent() { return this.text + this.kids.map(k => k.textContent).join(''); }
              remove() { if (this.parent) this.parent.kids = this.parent.kids.filter(k => k !== this); this.parent = null; }
              matches(selector) {
                return selector.split(',').map(s => s.trim()).some(s =>
                  s === this.tag || (s.startsWith('.') && this.cls.split(' ').includes(s.slice(1))) || (s === '[role=note]' && this.cls === 'role-note'));
              }
              querySelectorAll(selector) {
                const found = [];
                const walk = node => { for (const k of node.kids) { if (k.matches(selector)) found.push(k); walk(k); } };
                walk(this);
                return found;
              }
            }
            const root = new N('div', '', '', [
              new N('table', 'infobox', '', [new N('p', '', '48° 12′ 30″ N, 16° 22′ 23″ LFuncionamentoEstatuto e mais texto da caixa')]),
              new N('div', 'hatnote', '', [new N('p', 'role-note', 'Nota: não confundir com a Europa insular, que é outra coisa bem diferente.')]),
              new N('p', '', '', [new N('', '', 'Geografia'), new N('span', 'mw-editsection', '[editar | editar código-fonte] e mais umas palavras')]),
              new N('p', '', 'A Europa continental é a parte   continental da Europa,', [new N('sup', '', '[1]'), new N('', '', '\n excluindo as ilhas.')]),
              new N('p', '', 'Curto.'),
              new N('figure', '', '', [new N('figcaption', '', 'Mapa da Europa continental com as ilhas a cinzento e o resto a azul.')]),
              new N('p', '', 'No Reino Unido, o termo «o Continente» designa muitas vezes a Europa continental.'),
            ]);
            """;
        var output = NodeRunner.Run(LinkPreview.CollectParasScript + "\n" + fakeDom + "\nprocess.stdout.write(JSON.stringify(collectParas(root)));");
        var paras = JsonSerializer.Deserialize<string[]>(output)!;
        Assert.Equal(
        [
            "A Europa continental é a parte continental da Europa, excluindo as ilhas.",
            "No Reino Unido, o termo «o Continente» designa muitas vezes a Europa continental.",
        ], paras);
    }

    [Fact]
    public void CollectParas_StopsAt60AndTheBudget_AndToleratesNoRoot()
    {
        const string fake = """
            const p = text => ({ textContent: text });
            const many = { querySelectorAll: s => s === 'p' ? Array.from({ length: 100 }, (_, i) => p('Parágrafo ' + i + ' com texto suficiente para passar o mínimo.')) : [] };
            const huge = { querySelectorAll: s => s === 'p' ? Array.from({ length: 10 }, () => p('x'.repeat(4000))) : [] };
            process.stdout.write([collectParas(many).length, collectParas(huge).length, collectParas(null).length, collectParas({}).length].join(','));
            """;
        Assert.Equal("60,5,0,0", NodeRunner.Run(LinkPreview.CollectParasScript + "\n" + fake));
    }

    // ---------- Proposta 1 «Espreitar a sério»: o cartão ----------

    [Theory]
    [InlineData("Wikipédia", "W")]
    [InlineData("exemplo.pt", "E")]
    [InlineData("  «Público»", "P")]
    [InlineData("ébano.pt", "É")]
    [InlineData("1 2 3 Zé", "1")]
    [InlineData("", "?")]
    [InlineData("…", "?")]
    public void Monogram_IsTheFirstLetterOfTheSite(string site, string expected) => Assert.Equal(expected, LinkPreview.Monogram(site));

    [Fact]
    public void ExpandButton_SaysWhatItDoes()
    {
        Assert.Equal("Ler tudo aqui", LinkPreview.ExpandLabel(expanded: false));
        Assert.Equal("Mostrar menos", LinkPreview.ExpandLabel(expanded: true));
        Assert.Equal("Mostrar o artigo inteiro aqui (E)", LinkPreview.ExpandTip(expanded: false));
        Assert.Equal("Voltar ao resumo (E)", LinkPreview.ExpandTip(expanded: true));
        Assert.Equal("Sem resumo para esta página — «Abrir» mostra-a", LinkPreview.NoSummaryStatus);
    }

    // O cartão descreve a página que foi lida: depois de um redirecionamento (http → https, encurtador, redirecionamento
    // aberto), o endereço final; sem endereço web confirmado, o do link.
    [Theory]
    [InlineData("http://publico.pt/x", "https://www.publico.pt/x", "https://www.publico.pt/x")]
    [InlineData("https://t.co/abc", "https://exemplo.pt/artigo", "https://exemplo.pt/artigo")]
    [InlineData("https://exemplo.pt/a", "https://exemplo.pt/a", "https://exemplo.pt/a")]
    [InlineData("https://exemplo.pt/a", "chrome-error://chromewebdata/", "https://exemplo.pt/a")]
    [InlineData("https://exemplo.pt/a", "data:text/html,oi", "https://exemplo.pt/a")]
    [InlineData("https://exemplo.pt/a", "", "https://exemplo.pt/a")]
    [InlineData("https://exemplo.pt/a", null, "https://exemplo.pt/a")]
    public void PageUrl_IsTheLoadedAddress_WhenItIsAWebPage(string link, string? loaded, string expected)
        => Assert.Equal(expected, LinkPreview.PageUrl(link, loaded));

    // Correções da revisão: o ícone e o nome do site saem da página lida (não do link) e da base, não da cache de ícones
    // da janela (que não sabe de um histórico apagado), lidos uma só vez, quando a página chega; a dica de «Ler tudo aqui»
    // volta ao princípio com cada cartão; a E premida não alterna sem parar; o favicon tem nome, como o monograma; o
    // leitor de ecrã ouve o estado e, com o resumo pronto, o título.
    [Fact]
    public void Card_DescribesTheLoadedPage_AndResetsItsHints()
    {
        var code = Source("MainWindow.Glance.cs");
        var extract = Method(code, "private async Task ExtractGlanceAsync(");
        Assert.Contains("var page = LinkPreview.PageUrl(url, loadedUrl);", extract);
        Assert.Contains("LinkPreview.Parse(response.Result as string, page)", extract);
        Assert.Contains("LoadGlanceImage(browser, image, page, serial)", extract);
        Assert.DoesNotContain("CachedSiteIcon", code);
        Assert.DoesNotContain("_siteIconCache", code);
        Assert.Contains("ShowGlanceSiteIcon(null, GlanceSite.Text);", Method(code, "private void ResetGlanceCard("));
        Assert.Contains("AppData.SiteIcons([pageUrl])", Method(code, "private void ShowGlanceSiteIcon("));
        Assert.Contains("AutomationProperties.SetName(GlanceFavicon, site);", code);

        var fill = Method(code, "private void FillGlance(");
        Assert.Contains("GlanceExpand.ToolTip = LinkPreview.ExpandTip(expanded: false);", fill);
        Assert.Contains("Sync.LiveRegion.Announce(GlanceTitle)", fill);
        Assert.Contains("GlanceExpand.ToolTip = LinkPreview.ExpandTip(_glanceExpanded);", Method(code, "private void ToggleGlanceExpand("));
        Assert.DoesNotContain("\"Voltar ao resumo (E)\"", code);
        Assert.Contains("if (action == GlanceAction.Expand && e.IsRepeat)", Method(code, "private void GlanceOverlay_KeyDown("));
        Assert.Contains("if (text is not null) Sync.LiveRegion.Announce(GlanceStatusText);", Method(code, "private void SetGlanceStatus("));

        var xaml = Source("MainWindow.xaml");
        Assert.Matches("x:Name=\"GlanceTitle\"[^>]*AutomationProperties.LiveSetting=\"Polite\"", xaml);
    }

    // O corpo de um método do código-fonte: da assinatura até à chaveta que a fecha, à mesma indentação.
    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        return code[start..end];
    }

    // O cartão: favicon (ou monograma) antes do site, «Ler tudo aqui» à esquerda de «Abrir ao lado», o estado como região
    // viva educada; a tecla E e o botão fazem o mesmo; o estado sem resumo diz o que fazer.
    [Fact]
    public void Card_HasFaviconExpandAndAPoliteStatus()
    {
        var xaml = Source("MainWindow.xaml");
        var card = xaml[xaml.IndexOf("x:Name=\"GlanceCard\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"MiniBar\"", StringComparison.Ordinal)];
        var favicon = card.IndexOf("x:Name=\"GlanceFavicon\"", StringComparison.Ordinal);
        Assert.True(favicon >= 0);
        Assert.True(favicon < card.IndexOf("x:Name=\"GlanceSite\"", StringComparison.Ordinal));
        Assert.Contains("x:Name=\"GlanceMonogram\"", card);
        var expand = card.IndexOf("x:Name=\"GlanceExpand\"", StringComparison.Ordinal);
        Assert.True(expand >= 0);
        Assert.True(expand < card.IndexOf("Content=\"Abrir ao lado\"", StringComparison.Ordinal));
        Assert.Contains("ToolTip=\"Mostrar o artigo inteiro aqui (E)\"", card);
        Assert.Matches("x:Name=\"GlanceExpand\"[^>]*Visibility=\"Collapsed\"", card);
        Assert.Matches("x:Name=\"GlanceStatusText\"[^>]*AutomationProperties.LiveSetting=\"Polite\"", card);

        var code = Source("MainWindow.Glance.cs");
        Assert.Contains("case GlanceAction.Expand: ToggleGlanceExpand(); break;", code);
        Assert.Contains("GlanceExpand.Visibility = info.HasMore ? Visibility.Visible : Visibility.Collapsed;", code);
        Assert.Contains("LinkPreview.NoSummaryStatus", code);
        Assert.DoesNotContain("Esta página não tem texto para mostrar", code);
        // Um Border não tem par de automação: o nome do cartão também vai no ScrollViewer, e o do monograma no texto.
        Assert.Contains("AutomationProperties.SetName(GlanceCard, \"Espreitar: \" + ", code);
        Assert.Contains("AutomationProperties.SetName(GlanceScrollViewer, \"Espreitar: \" + ", code);
        Assert.Contains("AutomationProperties.SetName(GlanceMonogramText, ", code);
        // O favicon só vem da cache local de ícones do histórico (a mesma das listas), também na janela privada, que não
        // escreve nela (ver Card_DescribesTheLoadedPage_AndResetsItsHints).
        Assert.Contains("AppData.SiteIcons([pageUrl])", code);
    }

    private static string Source(string file, [System.Runtime.CompilerServices.CallerFilePath] string caller = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(caller)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Eteris.Browser", file));
    }
}
