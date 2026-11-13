using System.Text.Json;

namespace Eteris.Browser.Watching;

// O recorte ao vivo (ClipWindow): o script que deixa à vista só o pedaço escolhido, encostado ao canto da janela.
public static class ClipScripts
{
    // Uma expressão (uma promessa): procura o elemento (até ~10 s, a página pode pô-lo por JavaScript) e devolve, em texto
    // JSON, {"w","h"} em px CSS — ou null se ele não aparecer (a janela mostra então a página inteira, como está).
    // - Esconde o resto: visibility: hidden em tudo menos no elemento (e no que está dentro dele) e no caminho dos
    //   antepassados; os elementos position: fixed (barras, avisos de cookies) ficam escondidos com o resto.
    // - html { overflow: hidden }, sem barras; o elemento fica na origem da janela com um translate no html (não mexe na
    //   disposição da página), refeito por um ResizeObserver (o elemento ou a página mudam de tamanho) e de 1 em 1 s
    //   (algo por cima dele cresceu). Uma página que troca o elemento por outro igual volta a ser apanhada.
    // O seletor entra como texto JSON (JsonSerializer.Serialize), nunca colado.
    public static string Isolate(string selector)
        => "(async () => {\nconst selector = " + JsonSerializer.Serialize(selector) + ";\n" + IsolateBody + "\n})()";

    private const string IsolateBody = """
        const key = Symbol.for('eteris.recorte');
        if (window[key]) window[key].stop();
        const find = () => { try { return document.querySelector(selector); } catch (e) { return null; } };
        let el = find();
        for (let i = 0; !el && i < 20; i++) {
          await new Promise(r => setTimeout(r, 500));
          el = find();
        }
        if (!el) return null;

        const root = document.documentElement;
        const PIECE = 'data-eteris-recorte', PATH = 'data-eteris-recorte-caminho';
        const mark = () => {
          document.querySelectorAll('[' + PIECE + '], [' + PATH + ']').forEach(n => { n.removeAttribute(PIECE); n.removeAttribute(PATH); });
          el.setAttribute(PIECE, '');
          for (let p = el.parentElement; p; p = p.parentElement) p.setAttribute(PATH, '');
        };
        mark();

        const style = document.createElement('style');
        const move = document.createElement('style');
        style.textContent = `
          html { overflow: hidden !important; scrollbar-width: none !important; }
          html::-webkit-scrollbar { display: none !important; }
          body *:not([${PATH}]):not([${PIECE}]):not([${PIECE}] *) { visibility: hidden !important; }
        `;
        (document.head || root).append(style, move);

        let tx = 0, ty = 0;
        const place = () => {
          if (!el.isConnected) {
            const again = find();
            if (!again) return;
            el = again;
            mark();
            observer.observe(el);
          }
          if (!style.isConnected) (document.head || root).append(style, move);
          if (window.scrollX || window.scrollY) window.scrollTo(0, 0);
          const r = el.getBoundingClientRect();
          if (Math.abs(r.left) < 0.5 && Math.abs(r.top) < 0.5) return;
          tx -= r.left;
          ty -= r.top;
          move.textContent = `html { transform: translate(${tx}px, ${ty}px) !important; }`;
        };
        const observer = new ResizeObserver(place);
        observer.observe(el);
        observer.observe(root);
        addEventListener('resize', place);
        const timer = setInterval(place, 1000);
        window[key] = {
          stop() {
            observer.disconnect();
            removeEventListener('resize', place);
            clearInterval(timer);
            style.remove();
            move.remove();
          },
        };
        place();
        const r = el.getBoundingClientRect();
        return JSON.stringify({ w: r.width, h: r.height });
        """;
}
