using System.IO;
using System.Text.Json;
using CefSharp;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;
using Eteris.Browser.Tests.Sync;
using static Eteris.Browser.Tests.PrivacyRequestHandlerTests;
using PrivacyResourceHandler = Eteris.Browser.Shields.PrivacyRequestHandler.PrivacyResourceHandler;

namespace Eteris.Browser.Tests;

// «Escudo sem buracos» (proposta do ciclo da pele nova): o Escudo travava o anúncio e deixava o lugar dele vazio — no
// Público, um bloco de 291 px só com «PUBLICIDADE» (o rótulo vem de um ::before do <ad-placement>), no Observador um de
// 290 px com o mesmo rótulo. Agora recolhem-se, neste computador: os iframe e img da página cujo pedido o Escudo bloqueou,
// e os contentores que ficaram só com um rótulo «Publicidade», «Anúncio» ou «Pub». A exceção por site desliga tudo.
public class ShieldHolesTests
{
    // ---- o núcleo (Holes), no Node, sobre uma árvore de imitação ----------------------------------------------------

    private static string RunCore(string body)
        => NodeRunner.Run(CosmeticScripts.HolesCore + "\n" + body).Trim();

    [Theory]
    [InlineData("Publicidade", true)]
    [InlineData("PUBLICIDADE", true)]
    [InlineData("  Publicidade \n", true)]
    [InlineData("\"publicidade\"", true)] // o content de um ::before, com aspas
    [InlineData("Anúncio", true)]
    [InlineData("ANÚNCIO", true)]
    [InlineData("Pub", true)]
    [InlineData("PUB", true)]
    [InlineData("Publicidades", false)]
    [InlineData("Publicidade institucional", false)]
    [InlineData("Anúncios classificados", false)]
    [InlineData("Pub crawl", false)]
    [InlineData("", false)]
    [InlineData("none", false)]
    [InlineData("counter(x)", false)]
    public void Label_OnlyTheThreeAdLabels(string text, bool expected)
        => Assert.Equal(expected ? "true" : "false", RunCore($"console.log(!!Holes.label({JsonSerializer.Serialize(text)}))"));

    // A árvore de imitação: cada nó tem tag, altura, se tem outro conteúdo à vista (filled), papel e pai.
    private const string Tree = """
        const N = (tag, h, o = {}) => ({ tag, h, filled: !!o.filled, role: o.role || null, editable: !!o.editable, parent: o.parent || null, name: o.name || tag });
        const dom = { parent: e => e.parent, tag: e => e.tag, role: e => e.role, editable: e => e.editable, filled: e => e.filled, height: e => e.h };
        const box = (label, lh) => { const b = Holes.box(label, lh, dom); return b ? b.name : 'nada'; };
        const html = N('HTML', 5000, { filled: true }), body = N('BODY', 5000, { filled: true, parent: html });
        """;

    private static string Box(string build) => RunCore(Tree + build);

    // Público: <div id="banner-and-header"> (com o cabeçalho) > <ad-placement> de 291 px com o rótulo num ::before.
    [Fact]
    public void Publico_TheAdPlacementWithTheLabelInABefore_Collapses()
        => Assert.Equal("ad-placement", Box("""
            const banner = N('DIV', 476, { filled: true, parent: body });
            const ad = N('AD-PLACEMENT', 291, { parent: banner, name: 'ad-placement' });
            console.log(box(ad, 18));
            """));

    // Observador: <header> > <div class="obs-ad-placeholder"> de 290 px (só com um <script> e o rótulo num ::before).
    [Fact]
    public void Observador_ThePlaceholderCollapses_TheHeaderNever()
        => Assert.Equal("placeholder", Box("""
            const header = N('HEADER', 460, { parent: body });
            const ad = N('DIV', 290, { parent: header, name: 'placeholder' });
            console.log(box(ad, 18));
            """));

    // O rótulo num elemento próprio dentro do lugar reservado: recolhe-se o contentor todo, até ao primeiro com conteúdo.
    [Fact]
    public void ALabelInsideAnEmptySlot_CollapsesTheWholeSlot()
        => Assert.Equal("slot", Box("""
            const column = N('DIV', 900, { filled: true, parent: body });
            const slot = N('DIV', 270, { parent: column, name: 'slot' });
            const inner = N('DIV', 250, { parent: slot });
            const label = N('SPAN', 16, { parent: inner });
            console.log(box(label, 16));
            """));

    [Fact]
    public void TheClimbStopsAtMainArticleBodyAndHtml_AndAfterSixLevels()
    {
        Assert.Equal("slot", Box("""
            const main = N('MAIN', 3000, { parent: body });
            const slot = N('DIV', 250, { parent: main, name: 'slot' });
            const label = N('SPAN', 16, { parent: slot });
            console.log(box(label, 16));
            """));
        Assert.Equal("slot", Box("""
            const article = N('ARTICLE', 3000, { parent: body });
            const slot = N('DIV', 250, { parent: article, name: 'slot' });
            console.log(box(N('SPAN', 16, { parent: slot }), 16));
            """));
        Assert.Equal("slot", Box("""
            const slot = N('DIV', 250, { parent: body, name: 'slot' });
            console.log(box(N('SPAN', 16, { parent: slot }), 16));
            """));
        Assert.Equal("6", Box("""
            let e = N('SPAN', 16, { parent: null, name: '0' }); const label = e;
            for (let i = 1; i <= 9; i++) { const p = N('DIV', 250, { parent: body, name: String(i) }); e.parent = p; e = p; }
            console.log(box(label, 16));
            """));
        Assert.Equal("nada", Box("console.log(box(body, 16));"));
    }

    // O rótulo como texto a sério nunca se esconde: numa ligação, num botão, num título, num menu de navegação, num
    // campo editável; nem um contentor com mais alguma coisa à vista; nem um rótulo sozinho sem lugar reservado.
    [Theory]
    [InlineData("const a = N('A', 300, { parent: body }); console.log(box(N('SPAN', 16, { parent: a }), 16));")]
    [InlineData("const b = N('BUTTON', 300, { parent: body }); console.log(box(N('SPAN', 16, { parent: b }), 16));")]
    [InlineData("console.log(box(N('H2', 60, { parent: body }), 40));")]
    [InlineData("const d = N('DIV', 300, { parent: body, role: 'heading' }); console.log(box(N('SPAN', 16, { parent: d }), 16));")]
    [InlineData("const d = N('DIV', 300, { parent: body, role: 'link' }); console.log(box(N('SPAN', 16, { parent: d }), 16));")]
    [InlineData("const nav = N('NAV', 300, { parent: body }); const d = N('DIV', 300, { parent: nav }); console.log(box(N('SPAN', 16, { parent: d }), 16));")]
    [InlineData("const d = N('DIV', 300, { parent: body, editable: true }); console.log(box(N('SPAN', 16, { parent: d, editable: true }), 16));")]
    [InlineData("console.log(box(N('DIV', 300, { parent: body, filled: true }), 16));")]
    [InlineData("const d = N('DIV', 300, { parent: body, filled: true }); console.log(box(N('SPAN', 16, { parent: d }), 16));")]
    [InlineData("const d = N('DIV', 22, { parent: body }); console.log(box(N('SPAN', 16, { parent: d }), 16));")]
    [InlineData("console.log(box(N('DIV', 48, { parent: body }), 40));")]
    [InlineData("const li = N('LI', 18, { parent: body }); console.log(box(N('SPAN', 16, { parent: li }), 16));")]
    public void RealText_NeverCollapses(string build)
        => Assert.Equal("nada", Box(build));

    [Fact]
    public void Hit_MatchesTheBlockedAddress_WithoutTheFragment()
    {
        Assert.Equal("true true false false false", RunCore("""
            const blocked = Object.create(null);
            blocked['https://ad.doubleclick.net/ddm/x;ord=1'] = 1;
            console.log([
              Holes.hit(blocked, [undefined, 'https://ad.doubleclick.net/ddm/x;ord=1']),
              Holes.hit(blocked, ['https://ad.doubleclick.net/ddm/x;ord=1#f', '']),
              Holes.hit(blocked, ['https://ad.doubleclick.net/ddm/x;ord=2']),
              Holes.hit(blocked, ['', null, undefined]),
              Holes.hit(blocked, ['toString']),
            ].join(' '));
            """));
    }

    // ---- o script inteiro, no Node, sobre um DOM de imitação ---------------------------------------------------------

    // Um DOM mínimo: o que o script usa (querySelectorAll, TreeWalker de texto, getComputedStyle com ::before/::after,
    // createRange, caixas). Um elemento com display:none (inline) deixa de ter caixa, como no motor.
    private const string FakeDom = """
        const hidden = e => { for (let x = e; x; x = x.parentElement) if (x.style.display === 'none') return true; return false; };
        class Text { constructor(v) { this.nodeValue = v; this.parentElement = null; } }
        class El {
          constructor(tag, o = {}) {
            Object.assign(this, { tagName: tag.toUpperCase(), children: [], childNodes: [], parentElement: null, h: o.h ?? 20, w: o.w ?? 300,
              attrs: o.attrs || {}, before: o.before || 'none', after: o.after || 'none', bg: o.bg || 'none', isContentEditable: false,
              src: o.src || '', currentSrc: o.currentSrc, naturalWidth: o.naturalWidth ?? 0, id: o.id || '' });
            this.style = { display: '', setProperty: (k, v, p) => { if (k === 'display' && p === 'important') this.style.display = v; } };
          }
          add(...kids) { for (const k of kids) { k.parentElement = this; this.childNodes.push(k); if (k instanceof El) this.children.push(k); } return this; }
          getAttribute(n) { return this.attrs[n] ?? null; }
          getBoundingClientRect() { return hidden(this) ? { width: 0, height: 0 } : { width: this.w, height: this.h }; }
          getClientRects() { return hidden(this) ? [] : [this.getBoundingClientRect()]; }
          all() { return this.children.flatMap(c => [c, ...c.all()]); }
          getElementsByTagName() { return this.all(); }
          querySelectorAll(sel) {
            const parts = sel.split(',').map(s => s.trim());
            const ok = e => parts.some(p => p === '*' || p.toUpperCase() === e.tagName || (/^(\w+)\[(\w+)\]$/.test(p) && RegExp.$1.toUpperCase() === e.tagName && e.attrs[RegExp.$2] != null));
            return this.all().filter(ok);
          }
        }
        const el = (tag, o, ...kids) => new El(tag, o).add(...kids.map(k => typeof k === 'string' ? new Text(k) : k));
        const textNodes = e => e.childNodes.flatMap(k => k instanceof Text ? [k] : textNodes(k));
        globalThis.NodeFilter = { SHOW_TEXT: 4 };
        globalThis.getComputedStyle = (e, pseudo) => ({
          content: pseudo === '::before' ? e.before : pseudo === '::after' ? e.after : 'normal',
          backgroundImage: pseudo ? 'none' : e.bg, fontSize: '12px' });
        const page = body => {
          const html = el('html', { h: 5000 }, body);
          globalThis.document = {
            documentElement: html, body,
            querySelectorAll: sel => html.querySelectorAll(sel),
            createTreeWalker: root => { const list = textNodes(root); let i = -1; return { nextNode: () => list[++i] || null }; },
            createRange: () => { let n = null; return { selectNodeContents: x => { n = x; }, getBoundingClientRect: () => ({ height: n && !hidden(n.parentElement) ? 16 : 0 }) }; },
          };
          return html;
        };
        """;

    private static string RunPage(string build, IReadOnlyCollection<string> blocked, bool labels = true)
        => NodeRunner.Run(FakeDom + build + "\nconst collapsed = " + CosmeticScripts.CollapseHoles(blocked, labels).TrimEnd().TrimEnd(';') + ";\n"
            + "console.log(collapsed + ' ' + document.body.all().filter(e => e.style.display === 'none').map(e => e.id || e.tagName.toLowerCase()).join(','));").Trim();

    // O Público como a prova o mediu: o <ad-placement> vazio com «publicidade» num ::before, ao lado do cabeçalho;
    // as notícias, a ligação «Publicidade» do menu e a do rodapé ficam.
    [Fact]
    public void Script_Publico_CollapsesOnlyTheAdPlacement()
        => Assert.Equal("1 pubHorz0", RunPage("""
            page(el('body', { h: 13000 },
              el('div', { id: 'banner-and-header', h: 476 },
                el('ad-placement', { id: 'pubHorz0', h: 291, before: '"publicidade"' }),
                el('header', { h: 185 }, el('a', { attrs: { href: '/' } }, 'PÚBLICO'))),
              el('nav', { h: 40 }, el('ul', { h: 20 }, el('li', { h: 20 }, el('a', { attrs: { href: '/publicidade' }, h: 20 }, 'Publicidade')))),
              el('article', { h: 340 }, el('img', { id: 'foto', src: 'https://imagens.publico.pt/a.jpg', naturalWidth: 1000, h: 330 }), el('h2', { h: 30 }, 'Presidentes e cargas policiais')),
              el('footer', { h: 247 }, el('div', { h: 19 }, el('a', { attrs: { href: '/publicidade' }, h: 19 }, 'Publicidade')))));
            """, ["https://ad.doubleclick.net/ddm/trackimp/N1;ord=1"]));

    // Observador: o lugar do anúncio só com um <script> (que não conta como texto) e o rótulo num ::before.
    [Fact]
    public void Script_Observador_AScriptInsideTheSlotIsNotContent()
        => Assert.Equal("1 obs", RunPage("""
            page(el('body', { h: 18000 },
              el('header', { h: 460 },
                el('div', { id: 'obs', h: 290, before: '"Publicidade"' }, el('script', { h: 0 }, 'obs_ads.queue_slot({"unit":"web_manchete"})')),
                el('div', { h: 88 }, el('a', { attrs: { href: '/' }, h: 86 }, el('svg', { h: 86 })))),
              el('main', { h: 9000 }, el('p', { h: 40 }, 'Texto da notícia'))));
            """, []));

    // Os iframe e img bloqueados recolhem-se; depois disso, o contentor que só tinha o anúncio e o rótulo também.
    // Uma imagem partida que o Escudo não bloqueou (404) fica, com o texto alternativo; e uma imagem bloqueada que a
    // página trocou por outra (onerror) e carregou também fica.
    [Fact]
    public void Script_BlockedFramesAndImages_ThenTheirEmptySlots()
        => Assert.Equal("4 slot1,anuncio,banner,pixel", RunPage("""
            page(el('body', { h: 3000 },
              el('div', { id: 'slot1', h: 270 }, el('span', { h: 16 }, 'Anúncio'), el('iframe', { id: 'anuncio', src: 'https://googleads.g.doubleclick.net/pagead/ads?client=x', h: 250 })),
              el('main', { h: 2000 },
                el('p', { h: 40 }, 'Notícia'),
                el('img', { id: 'banner', src: 'https://ads.example/b.gif#x', h: 90, naturalWidth: 0 }),
                el('img', { id: 'pixel', currentSrc: 'https://ads.example/p.gif', src: 'https://ads.example/p.gif', h: 3, w: 3 }),
                el('img', { id: 'partida', src: 'https://site.pt/nao-existe.png', h: 90, naturalWidth: 0 }),
                el('img', { id: 'trocada', src: 'https://ads.example/fallback.png', h: 90, naturalWidth: 300 }),
                el('h2', { h: 60 }, 'Publicidade'),
                el('div', { h: 18 }, 'Pub'))));
            """, ["https://googleads.g.doubleclick.net/pagead/ads?client=x", "https://ads.example/b.gif", "https://ads.example/p.gif", "https://ads.example/fallback.png"]));

    // Sem nada bloqueado e sem rótulos, o script não toca em nada (e uma página sem body também não falha).
    [Fact]
    public void Script_LeavesAnOrdinaryPageAlone()
    {
        Assert.Equal("0", RunPage("""
            page(el('body', { h: 3000 },
              el('div', { h: 300, bg: 'url(x.png)' }, 'Publicidade'),
              el('div', { h: 250 }),
              el('div', { h: 250, bg: 'url(fundo.jpg)', before: '"Pub"' }),
              el('main', { h: 2000 }, el('p', { h: 40 }, 'Notícia'))));
            """, []));
    }

    // Os bloqueios que chegam depois do carregamento (labels: false): a página só volta a procurar rótulos se recolheu
    // algum iframe ou imagem — um beacon (new Image()) bloqueado que não está na página não faz percorrer a página toda.
    // Medido ao vivo: a procura dos rótulos custa 2–15 ms num jornal e ~40 ms numa página de 13 500 elementos.
    [Fact]
    public void Script_LateBlocks_SweepTheLabelsOnlyAfterCollapsingSomething()
    {
        const string Page = """
            page(el('body', { h: 3000 },
              el('div', { id: 'vazio', h: 250, before: '"Publicidade"' }),
              el('div', { id: 'slot', h: 270 }, el('span', { h: 16 }, 'Anúncio'), el('iframe', { id: 'f', src: 'https://ads.example/ad.html', h: 250 })),
              el('main', { h: 2000 }, el('p', { h: 40 }, 'Notícia'))));
            """;
        Assert.Equal("0", RunPage(Page, ["https://ads.example/beacon.gif"], labels: false));
        Assert.Equal("3 vazio,slot,f", RunPage(Page, ["https://ads.example/ad.html"], labels: false));
        Assert.Equal("1 vazio", RunPage(Page, ["https://ads.example/beacon.gif"], labels: true));
    }

    [Fact]
    public void CollapseHoles_EmbedsTheAddressesAsJson_AndTheyCannotBreakOut()
    {
        var script = CosmeticScripts.CollapseHoles(["https://a.example/x\"); alert(1); (\"</script>"]);
        Assert.DoesNotContain("\"); alert(1)", script);
        Assert.DoesNotContain("</script>", script);
        Assert.Contains("Holes.box", script);
    }

    // ---- de onde vêm os endereços: o tratador dos pedidos ------------------------------------------------------------

    private static IFrame Frame(bool main, IFrame? parent = null)
    {
        var (frame, fake) = Fake<IFrame>.Create();
        fake.Members["get_IsValid"] = _ => true;
        fake.Members["get_IsMain"] = _ => main;
        fake.Members["get_Url"] = _ => main ? "https://www.publico.pt/" : "about:blank";
        fake.Members["get_Parent"] = _ => parent;
        return frame;
    }

    private static IBrowser Browser(IFrame main)
    {
        var (browser, fake) = Fake<IBrowser>.Create();
        fake.Members["get_MainFrame"] = _ => main;
        return browser;
    }

    private static IRequest Req(string url, ResourceType type)
    {
        var (request, fake) = Fake<IRequest>.Create();
        fake.Members["get_Url"] = _ => url;
        fake.Members["get_ResourceType"] = _ => type;
        return request;
    }

    private sealed class Lists(bool block) : IRequestBlocker
    {
        public bool ShouldBlock(string requestUrl, string? pageUrl, string resourceType) => block;
    }

    private static (PrivacyResourceHandler Handler, List<string> Elements) Handler(bool block = true, bool shieldOn = true)
    {
        var elements = new List<string>();
        var handler = new PrivacyResourceHandler(() => new Lists(block), _ => shieldOn, _ => { }, null, onBlockedElement: elements.Add);
        return (handler, elements);
    }

    // Só as imagens do documento principal e os iframes filhos dele: são os que o script da página pode ver. O endereço
    // de um pedido feito dentro de um iframe de outro site nunca chega à página de cima.
    [Fact]
    public void OnlyImagesAndFramesOfTheMainDocument_AreReported()
    {
        var (handler, elements) = Handler();
        var main = Frame(main: true);
        var child = Frame(main: false, parent: main);
        var grandchild = Frame(main: false, parent: child);
        var browser = Browser(main);

        Assert.Equal(CefReturnValue.Cancel, handler.Decide(browser, main, Req("https://ads.example/b.gif", ResourceType.Image)));
        handler.Decide(browser, child, Req("https://ads.example/ad.html", ResourceType.SubFrame));
        handler.Decide(browser, main, Req("https://ads.example/a.js", ResourceType.Script));
        handler.Decide(browser, main, Req("https://ads.example/x", ResourceType.Xhr));
        handler.Decide(browser, child, Req("https://ads.example/dentro.gif", ResourceType.Image));
        handler.Decide(browser, grandchild, Req("https://ads.example/neto.html", ResourceType.SubFrame));

        Assert.Equal(["https://ads.example/b.gif", "https://ads.example/ad.html"], elements);
    }

    [Fact]
    public void NothingIsReported_WhenNothingIsBlocked_OrTheShieldIsOffForTheSite()
    {
        var main = Frame(main: true);
        var (allowing, none) = Handler(block: false);
        allowing.Decide(Browser(main), main, Req("https://cdn.example/foto.jpg", ResourceType.Image));
        Assert.Empty(none);

        var (off, nothing) = Handler(block: true, shieldOn: false);
        Assert.Equal(CefReturnValue.Continue, off.Decide(Browser(main), main, Req("https://ads.example/b.gif", ResourceType.Image)));
        Assert.Empty(nothing);
    }

    // Um frame destacado ou um aviso que lança: o pedido continua bloqueado e nada sai do tratador (thread de rede).
    [Fact]
    public void AFailingReport_NeverUnblocksNorThrows()
    {
        var main = Frame(main: true);
        var handler = new PrivacyResourceHandler(() => new Lists(true), _ => true, _ => { }, null,
            onBlockedElement: _ => throw new InvalidOperationException("janela a fechar"));
        Assert.Equal(CefReturnValue.Cancel, handler.Decide(Browser(main), main, Req("https://ads.example/b.gif", ResourceType.Image)));
        Assert.False(PrivacyResourceHandler.InMainDocument(ResourceType.Image, null));
        Assert.False(PrivacyResourceHandler.InMainDocument(ResourceType.SubFrame, Frame(main: true)));
    }

    // ---- a aba: só em memória, limitado, e recomeça com cada página --------------------------------------------------

    [Fact]
    public void TheTab_KeepsEachAddressOnce_UpToTheLimit_AndForgetsThemWithThePage()
    {
        var tab = new BrowserTab("https://www.publico.pt/");
        Assert.True(tab.RecordBlockedElement("https://ads.example/1.gif"));
        Assert.False(tab.RecordBlockedElement("https://ads.example/1.gif"));
        Assert.Equal(["https://ads.example/1.gif"], tab.BlockedElements);

        for (var i = 0; i < BrowserTab.RememberedBlockedElements + 10; i++) tab.RecordBlockedElement($"https://ads.example/{i}.png");
        Assert.Equal(BrowserTab.RememberedBlockedElements, tab.BlockedElements.Count);
        Assert.Equal($"https://ads.example/{BrowserTab.RememberedBlockedElements + 9}.png", tab.BlockedElements[^1]);

        tab.LeavePage("https://www.publico.pt/");
        Assert.Empty(tab.BlockedElements);
    }

    // ---- a ligação na janela ----------------------------------------------------------------------------------------

    private static string Source(string file) => File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", file)).Replace("\r\n", "\n");

    // Corre com as regras genéricas, nas duas passagens, só com o Escudo ligado no site (a exceção por site desliga-o) e
    // nunca na página do auto-teste; os endereços bloqueados chegam pela aba e voltam a recolher (sem rede) quando chegam.
    [Fact]
    public void TheWindow_CollapsesOnlyWithTheShieldOnForTheSite()
    {
        var shield = Source("MainWindow.Shield.cs");
        var generic = shield[shield.IndexOf("private async Task ApplyGenericCosmeticsAsync", StringComparison.Ordinal)..];
        generic = generic[..generic.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.True(generic.IndexOf("!ShieldOn(host)", StringComparison.Ordinal) is > 0 and var gate
            && generic.IndexOf("CosmeticScripts.CollapseHoles(", StringComparison.Ordinal) > gate);
        Assert.Contains("SelfTest.IsTestPage(url)", generic);

        var later = shield[shield.IndexOf("private void CollapseHoles(BrowserTab tab)", StringComparison.Ordinal)..];
        later = later[..later.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.True(later.IndexOf("!ShieldOn(host)", StringComparison.Ordinal) is > 0 and var gate2
            && later.IndexOf("CosmeticScripts.CollapseHoles(", StringComparison.Ordinal) > gate2);
        Assert.Contains("SelfTest.IsTestPage(", later);

        var window = Source("MainWindow.xaml.cs");
        Assert.Contains("onBlockedElement: url => Dispatcher.InvokeAsync(() => OnBlockedElement(tab, url))", window);
        // Um endereço repetido não volta a pôr a página a trabalhar, e o caminho tardio não procura rótulos à toa.
        Assert.Contains("if (!tab.RecordBlockedElement(url) || !_holesQueued.Add(tab)) return;", shield);
        Assert.Contains("CosmeticScripts.CollapseHoles(blocked, labels: false)", later);
        Assert.Contains("_ = ApplyGenericCosmeticsAsync(browser, url, tab);", window);
    }
}
