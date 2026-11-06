namespace Eteris.Browser.Shields;

// «Esconder elemento»: realça o elemento debaixo do rato; um clique esconde-o e devolve um seletor estável.
// Corre via EvaluateScriptAsPromiseAsync (o corpo é envolvido numa função async, daí o «return»).
public static class PickerScript
{
    public const string Pick = """
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

          const stable = c => c && c.length < 40 && !/\d{3,}|^(is|has)-|active|hover|focus|open|selected|visible|show/i.test(c);
          const esc = s => CSS.escape(s);
          const part = el => {
            if (el.id && stable(el.id) && document.querySelectorAll('#' + esc(el.id)).length === 1) return { sel: '#' + esc(el.id), unique: true };
            let sel = el.tagName.toLowerCase();
            const classes = [...el.classList].filter(stable).slice(0, 2);
            sel += classes.map(c => '.' + esc(c)).join('');
            const parent = el.parentElement;
            if (parent && !classes.length) {
              const same = [...parent.children].filter(c => c.tagName === el.tagName);
              if (same.length > 1) sel += `:nth-of-type(${same.indexOf(el) + 1})`;
            }
            return { sel, unique: false };
          };
          const selectorFor = el => {
            const parts = [];
            for (let node = el; node && node !== document.body && node !== document.documentElement && parts.length < 5; node = node.parentElement) {
              const p = part(node);
              parts.unshift(p.sel);
              if (p.unique || document.querySelectorAll(parts.join(' > ')).length === 1) break;
            }
            return parts.join(' > ');
          };

          let current = null;
          const move = e => {
            const el = document.elementFromPoint(e.clientX, e.clientY);
            if (!el || el === document.documentElement || el === document.body) return;
            current = el;
            const r = el.getBoundingClientRect();
            Object.assign(box.style, { left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
            label.textContent = selectorFor(el);
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
            const sel = selectorFor(current);
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
