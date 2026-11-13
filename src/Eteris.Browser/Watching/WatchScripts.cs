using System.Text.Json;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Watching;

// Os scripts da vigia na página. O seletor e a dica entram como texto JSON (JsonSerializer.Serialize), nunca colados:
// um seletor com aspas ou «</script>» não sai da sua string.
public static class WatchScripts
{
    // Lê a página e devolve JSON (texto) para o WatchReadingParser:
    // {"ld":[...],"itemprop":[...],"meta":[...],"currency":string|null,"sel":string|null,"selFound":bool,"title":string,
    //  "challenge":bool,"captcha":bool}
    // - ld: o texto em bruto de cada script[type="application/ld+json"];
    // - itemprop: o content (ou o texto) de cada [itemprop="price"]; meta: product:price:amount e og:price:amount;
    // - currency: a moeda que a página declara à parte (itemprop="priceCurrency", product:price:currency);
    // - sel: o texto do pedaço apontado (selFound diz se o seletor encontrou o elemento; um seletor inválido não encontra);
    // - challenge: a página de desafio (#challenge-form, #cf-challenge-running); captcha: um reCAPTCHA ou um iframe de
    //   CAPTCHA, que também aparece em formulários de páginas normais (o parser só o conta quando nada se leu).
    // Uma expressão (não um corpo async): corre com PageScript.EvaluateAsync sem asyncBody.
    public static string Read(string? selector) => "(() => {\nconst selector = " + JsonSerializer.Serialize(selector) + ";\n" + ReadBody + "\n})()";

    // Corpo async para PageScript.EvaluateAsync(..., asyncBody: true): realça o elemento debaixo do rato (com o texto que
    // vai ser vigiado) e, num clique, resolve para {"selector","text"} sem mexer na página; Esc resolve para null.
    public static string PickPiece(string tip)
        => "const tipText = " + JsonSerializer.Serialize(tip) + ";\n" + PickerScript.SelectorBuilder + "\n" + PickBody;

    private const string ReadBody = """
        const cap = (s, n) => typeof s === 'string' ? s.slice(0, n) : null;
        const all = q => Array.from(document.querySelectorAll(q));
        const valueOf = el => (el.hasAttribute('content') ? el.getAttribute('content') : el.textContent) || '';
        const ld = all('script[type="application/ld+json"]').slice(0, 20).map(s => cap(s.textContent, 200000)).filter(s => s);
        const itemprop = all('[itemprop="price"]').slice(0, 10).map(e => cap(valueOf(e).trim(), 200)).filter(s => s);
        const meta = all('meta[property="product:price:amount"], meta[property="og:price:amount"]').slice(0, 10)
          .map(m => cap((m.getAttribute('content') || '').trim(), 200)).filter(s => s);
        const currencyEl = document.querySelector('[itemprop="priceCurrency"], meta[property="product:price:currency"], meta[property="og:price:currency"]');
        const currency = currencyEl ? (cap(valueOf(currencyEl).trim(), 10) || null) : null;
        let sel = null, selFound = false;
        if (selector) {
          try {
            const el = document.querySelector(selector);
            if (el) {
              selFound = true;
              sel = cap(el.innerText ?? el.textContent ?? '', 4000);
            }
          } catch (e) { }
        }
        const challenge = !!document.querySelector('#challenge-form, #cf-challenge-running');
        const captcha = !!document.querySelector('iframe[src*="captcha" i], [class*="g-recaptcha"]');
        return JSON.stringify({ ld, itemprop, meta, currency, sel, selFound, title: cap(document.title || '', 500), challenge, captcha });
        """;

    private const string PickBody = """
        return new Promise(resolve => {
          const host = document.createElement('div');
          host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none;';
          const root = host.attachShadow({ mode: 'open' });
          const sheet = new CSSStyleSheet();
          sheet.replaceSync(`
            .box { position: fixed; border: 2px solid #8b5cf6; background: rgba(139,92,246,.16); border-radius: 4px;
                   box-shadow: 0 0 0 9999px rgba(5,7,16,.25); transition: all .06s ease-out; }
            .tip { position: fixed; left: 50%; top: 14px; transform: translateX(-50%); background: #1b1e28; color: #e9ebf2;
                   font: 13px "Segoe UI", system-ui, sans-serif; padding: 9px 16px; border-radius: 12px;
                   border: 1px solid #8b5cf6; box-shadow: 0 8px 30px rgba(0,0,0,.45); }
            .label { position: fixed; background: #8b5cf6; color: #fff; font: 12px "Segoe UI", system-ui, sans-serif; padding: 2px 7px;
                     border-radius: 4px; max-width: 60vw; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
          `);
          root.adoptedStyleSheets = [sheet];
          const box = document.createElement('div'); box.className = 'box';
          const tip = document.createElement('div'); tip.className = 'tip';
          tip.textContent = tipText;
          const label = document.createElement('div'); label.className = 'label';
          root.append(box, label, tip);
          document.documentElement.appendChild(host);

          const textOf = el => (el.innerText ?? el.textContent ?? '').trim();
          let current = null;
          const move = e => {
            const el = document.elementFromPoint(e.clientX, e.clientY);
            if (!el || el === document.documentElement || el === document.body) return;
            current = el;
            const r = el.getBoundingClientRect();
            Object.assign(box.style, { left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
            label.textContent = textOf(el).replace(/\s+/g, ' ').slice(0, 120) || '(sem texto)';
            Object.assign(label.style, { left: Math.max(0, r.left) + 'px', top: Math.max(0, r.top - 23) + 'px' });
          };
          const block = e => { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); };
          const finish = value => {
            removeEventListener('mousemove', move, true);
            ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup'].forEach(t => removeEventListener(t, t === 'click' ? click : block, true));
            removeEventListener('keydown', key, true);
            host.remove();
            resolve(value);
          };
          // A leitura usa querySelector (o 1.º elemento que o seletor encontra): um pedaço que se repete na página (um preço
          // numa lista) e cujo seletor encontra primeiro outro elemento não pode ser vigiado — seria lido o errado.
          const click = e => {
            block(e);
            if (!current) return;
            const selector = stableSelector(current);
            let same = false;
            try { same = document.querySelector(selector) === current; } catch (err) { }
            if (!same) {
              tip.textContent = 'Este pedaço repete-se na página e não dá para o vigiar sozinho · escolha outro ou Esc para cancelar';
              return;
            }
            finish({ selector, text: textOf(current).slice(0, 4000) });
          };
          const key = e => { if (e.key === 'Escape') { block(e); finish(null); } };
          addEventListener('mousemove', move, true);
          ['mousedown', 'mouseup', 'pointerdown', 'pointerup'].forEach(t => addEventListener(t, block, true));
          addEventListener('click', click, true);
          addEventListener('keydown', key, true);
        });
        """;
}
