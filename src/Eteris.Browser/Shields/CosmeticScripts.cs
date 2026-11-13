using System.Text.Json;

namespace Eteris.Browser.Shields;

public static class CosmeticScripts
{
    // Devolve "classes\nids" com tudo o que existe na página, para as regras genéricas (##.classe, ###id).
    public const string CollectClassesAndIds = """
        (function () {
          var classes = new Set(), ids = new Set();
          document.querySelectorAll('[class],[id]').forEach(function (e) {
            if (e.id) ids.add(e.id);
            if (typeof e.className === 'string') e.className.split(/\s+/).forEach(function (c) { if (c) classes.add(c); });
          });
          return Array.from(classes).join(' ') + '\n' + Array.from(ids).join(' ');
        })()
        """;

    // O CSS entra como literal JSON (o codificador por omissão escapa ", <, > e &): nada na lista sai da string.
    public static string Inject(string css) => $$"""
        (function (css) {
          var style = document.createElement('style');
          style.setAttribute('data-eteris-shield', '');
          style.textContent = css;
          (document.head || document.documentElement).appendChild(style);
        })({{JsonSerializer.Serialize(css)}});
        """;

    // Regras específicas do site, registadas para correr antes de qualquer script da página (como os scriptlets).
    // Nesse momento o documento ainda não tem elementos: espera-se pelo primeiro. Só na página principal.
    public static string InjectEarly(string css) => css.Length == 0 ? string.Empty : $$"""
        (function (css) {
          if (window.top !== window) return;
          var add = function () {
            var style = document.createElement('style');
            style.setAttribute('data-eteris-shield', '');
            style.textContent = css;
            (document.head || document.documentElement).appendChild(style);
          };
          if (document.documentElement) { add(); return; }
          new MutationObserver(function (_, observer) {
            if (!document.documentElement) return;
            observer.disconnect();
            add();
          }).observe(document, { childList: true });
        })({{JsonSerializer.Serialize(css)}});
        """;

    // ---- Escudo sem buracos -----------------------------------------------------------------------------------------
    // O Escudo trava o anúncio, mas o lugar dele ficava na página: no Público, um <ad-placement> de 291 px só com
    // «PUBLICIDADE» (num ::before); no Observador, um de 290 px igual. Recolhe-se, neste computador e sem pedidos:
    //  1. os iframe e img do documento principal cujo pedido o Escudo bloqueou (os endereços vêm da aba: blocked), uma
    //     imagem só se continuar partida (se a página a trocou por outra que carregou, fica);
    //  2. os contentores que ficaram só com um rótulo «Publicidade», «Anúncio» ou «Pub» (texto ou ::before/::after),
    //     sem mais texto, imagem, vídeo, controlo ou imagem de fundo à vista, e com lugar reservado (≥ 40 px e pelo menos
    //     16 px além do rótulo, sem contar o enchimento nem a borda: um cartão ou uma pastilha só altos pelo padding não
    //     são lugar de anúncio). Sobe-se no máximo 6 níveis e nunca até html, body, main, article, header, footer, nav,
    //     nem a uma célula, linha ou tabela, nem a um item ou lista (li, dt, dd, ul, ol, dl): esses nunca se escondem.
    // Um rótulo que é texto a sério nunca se esconde: dentro de uma ligação, botão, título, legenda, navegação, campo
    // editável ou de um controlo feito à mão (onclick, tabindex, cursor de mão), e numa célula ou item de lista com o
    // texto todo «Publicidade» (uma rubrica de despesas, uma categoria). Esconder é o display:none !important no próprio
    // elemento, sem marcas com o nome do Éteris.

    // O núcleo, sem DOM (testado no Node): label(texto) e box(rótulo, altura do rótulo, acessos ao DOM) e hit(endereços).
    public const string HolesCore = """
        var Holes = {
          LABELS: ['publicidade', 'anúncio', 'pub'],
          label: function (text) {
            var t = String(text == null ? '' : text).replace(/^["'“”«»\s]+|["'“”«»\s]+$/g, '').replace(/\s+/g, ' ').toLowerCase();
            return Holes.LABELS.indexOf(t) >= 0 ? t : '';
          },
          INSIDE: /^(A|BUTTON|LABEL|SELECT|OPTION|TEXTAREA|SUMMARY|NAV|H[1-6]|CAPTION|FIGCAPTION|LEGEND)$/,
          ROLES: /^(heading|button|link|menuitem|tab|option|navigation)$/,
          STOP: /^(HTML|BODY|MAIN|ARTICLE|HEADER|FOOTER|NAV|TABLE|THEAD|TBODY|TFOOT|TR|TD|TH|UL|OL|DL|LI|DT|DD)$/,
          MIN: 40,
          // dom: parent(e), tag(e) em maiúsculas, role(e), editable(e), interactive(e) (um controlo feito à mão), filled(e)
          // (outro conteúdo à vista), height(e) (a altura sem o enchimento nem a borda).
          box: function (label, labelHeight, dom) {
            for (var e = label; e; e = dom.parent(e))
              if (Holes.INSIDE.test(dom.tag(e)) || Holes.ROLES.test(dom.role(e) || '') || dom.editable(e) || dom.interactive(e)) return null;
            if (Holes.STOP.test(dom.tag(label)) || dom.filled(label)) return null;
            var box = label;
            for (var i = 0; i < 6; i++) {
              var p = dom.parent(box);
              if (!p || Holes.STOP.test(dom.tag(p)) || dom.filled(p)) break;
              box = p;
            }
            var h = dom.height(box);
            return h >= Holes.MIN && h >= labelHeight + 16 ? box : null;
          },
          bare: function (url) {
            url = url == null ? '' : String(url);
            var i = url.indexOf('#');
            return i < 0 ? url : url.slice(0, i);
          },
          hit: function (blocked, urls) {
            for (var i = 0; i < urls.length; i++) if (urls[i] && blocked[Holes.bare(urls[i])] === 1) return true;
            return false;
          }
        };
        """;

    // O script para a página (só o documento principal: PageScript). Devolve quantos elementos recolheu. labels: false
    // nos bloqueios que chegam depois do carregamento — os rótulos só se voltam a procurar se recolheu algum iframe ou img
    // (a procura percorre a página: 2–15 ms num jornal, ~40 ms numa página de 13 500 elementos, medido ao vivo).
    public static string CollapseHoles(IReadOnlyCollection<string> blocked, bool labels = true) => $$"""
        (function (list, sweep) {
          {{HolesCore}}
          var hidden = 0;
          var hide = function (e) {
            if (e.style.getPropertyValue && e.style.getPropertyValue('display') === 'none') return;
            e.style.setProperty('display', 'none', 'important');
            hidden++;
          };
          var blocked = Object.create(null);
          for (var i = 0; i < list.length; i++) blocked[Holes.bare(list[i])] = 1;
          if (list.length) document.querySelectorAll('iframe, img').forEach(function (e) {
            if (e.tagName === 'IMG' && e.naturalWidth > 0) return;
            if (Holes.hit(blocked, [e.currentSrc, e.src])) hide(e);
          });
          var body = document.body;
          if (!body || (!sweep && hidden === 0)) return hidden;
          var rendered = function (e) { var r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
          var filled = new Set();
          var mark = function (e) { for (; e && !filled.has(e); e = e.parentElement) filled.add(e); };
          var SKIP = /^(SCRIPT|STYLE|NOSCRIPT|TEMPLATE)$/;
          var labels = [];
          var walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
          for (var n = walker.nextNode(); n; n = walker.nextNode()) {
            var p = n.parentElement;
            if (!p || SKIP.test(p.tagName) || !/\S/.test(n.nodeValue) || p.getClientRects().length === 0) continue;
            if (!Holes.label(n.nodeValue)) { mark(p); continue; }
            var range = document.createRange();
            range.selectNodeContents(n);
            labels.push([p, range.getBoundingClientRect().height]);
          }
          body.querySelectorAll('img, svg, video, audio, canvas, iframe, object, embed, input, button, select, textarea, a[href]')
            .forEach(function (e) { if (rendered(e)) mark(e); });
          body.querySelectorAll('*').forEach(function (e) {
            if (filled.has(e) || SKIP.test(e.tagName) || !rendered(e)) return;
            ['::before', '::after'].forEach(function (which) {
              var s = getComputedStyle(e, which);
              if (Holes.label(s.content)) labels.push([e, (parseFloat(s.fontSize) || 0) * 1.5]);
            });
          });
          var background = function (e) { return getComputedStyle(e).backgroundImage !== 'none'; };
          var dom = {
            parent: function (e) { return e.parentElement; },
            tag: function (e) { return e.tagName; },
            role: function (e) { return e.getAttribute('role'); },
            editable: function (e) { return !!e.isContentEditable; },
            interactive: function (e) {
              return e.hasAttribute('onclick') || e.hasAttribute('tabindex') || getComputedStyle(e).cursor === 'pointer';
            },
            filled: function (e) {
              if (filled.has(e) || background(e)) return true;
              var all = e.getElementsByTagName('*');
              for (var i = 0; i < all.length; i++) if (rendered(all[i]) && background(all[i])) return true;
              return false;
            },
            height: function (e) {
              var s = getComputedStyle(e), px = function (v) { return parseFloat(v) || 0; };
              return e.getBoundingClientRect().height - px(s.paddingTop) - px(s.paddingBottom) - px(s.borderTopWidth) - px(s.borderBottomWidth);
            }
          };
          labels.forEach(function (l) { var b = Holes.box(l[0], l[1], dom); if (b) hide(b); });
          return hidden;
        })({{JsonSerializer.Serialize(blocked)}}, {{(labels ? "true" : "false")}});
        """;

    public static (string[] Classes, string[] Ids) ParseClassesAndIds(string raw)
    {
        var lines = raw.Split('\n');
        static string[] Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (Words(lines[0]), lines.Length > 1 ? Words(lines[1]) : []);
    }
}
