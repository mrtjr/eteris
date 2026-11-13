namespace Eteris.Browser.Shields;

// «Esconder elemento»: realça o elemento debaixo do rato; um clique esconde-o e devolve um seletor estável.
// Corre via PageScript.EvaluateAsync(asyncBody: true): o corpo é envolvido numa função async, daí o «return».
public static class PickerScript
{
    // stableSelector(el): um seletor curto que volta a encontrar o elemento — o id (se estável e único) ou a etiqueta
    // com até 2 classes estáveis (ou :nth-of-type), subindo até 5 níveis até ser único. Partilhado com a escolha do
    // pedaço da vigia (WatchScripts.PickPiece).
    public const string SelectorBuilder = """
        function stableSelector(el) {
          const stable = c => c && c.length < 40 && !/\d{3,}|^(is|has)-|active|hover|focus|open|selected|visible|show/i.test(c);
          const esc = s => CSS.escape(s);
          const part = node => {
            if (node.id && stable(node.id) && document.querySelectorAll('#' + esc(node.id)).length === 1) return { sel: '#' + esc(node.id), unique: true };
            let sel = node.tagName.toLowerCase();
            const classes = [...node.classList].filter(stable).slice(0, 2);
            sel += classes.map(c => '.' + esc(c)).join('');
            const parent = node.parentElement;
            if (parent && !classes.length) {
              const same = [...parent.children].filter(c => c.tagName === node.tagName);
              if (same.length > 1) sel += `:nth-of-type(${same.indexOf(node) + 1})`;
            }
            return { sel, unique: false };
          };
          const parts = [];
          for (let node = el; node && node !== document.body && node !== document.documentElement && parts.length < 5; node = node.parentElement) {
            const p = part(node);
            parts.unshift(p.sel);
            if (p.unique || document.querySelectorAll(parts.join(' > ')).length === 1) break;
          }
          return parts.join(' > ');
        }
        """;

    public const string Pick = SelectorBuilder + "\n" + """
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
            .label { position: fixed; background: #8b5cf6; color: #fff; font: 11px Consolas, monospace; padding: 2px 7px;
                     border-radius: 4px; max-width: 60vw; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
          `);
          root.adoptedStyleSheets = [sheet];
          const box = document.createElement('div'); box.className = 'box';
          const tip = document.createElement('div'); tip.className = 'tip';
          tip.textContent = 'Clique no elemento a esconder neste site · Esc para cancelar';
          const label = document.createElement('div'); label.className = 'label';
          root.append(box, label, tip);
          document.documentElement.appendChild(host);

          let current = null;
          const move = e => {
            const el = document.elementFromPoint(e.clientX, e.clientY);
            if (!el || el === document.documentElement || el === document.body) return;
            current = el;
            const r = el.getBoundingClientRect();
            Object.assign(box.style, { left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
            label.textContent = stableSelector(el);
            Object.assign(label.style, { left: Math.max(0, r.left) + 'px', top: Math.max(0, r.top - 21) + 'px' });
          };
          const block = e => { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); };
          const finish = value => {
            removeEventListener('mousemove', move, true);
            ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup'].forEach(t => removeEventListener(t, t === 'click' ? click : block, true));
            removeEventListener('keydown', key, true);
            host.remove();
            resolve(value);
          };
          const click = e => {
            block(e);
            if (!current) return;
            const sel = stableSelector(current);
            current.style.setProperty('display', 'none', 'important');
            finish(sel);
          };
          const key = e => { if (e.key === 'Escape') { block(e); finish(null); } };
          addEventListener('mousemove', move, true);
          ['mousedown', 'mouseup', 'pointerdown', 'pointerup'].forEach(t => addEventListener(t, block, true));
          addEventListener('click', click, true);
          addEventListener('keydown', key, true);
        });
        """;
}
