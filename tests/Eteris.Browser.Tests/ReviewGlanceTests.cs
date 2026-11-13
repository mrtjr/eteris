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
}
