namespace Eteris.Browser.Browser;

// Seguir links pelo teclado: cada link/botão visível recebe uma etiqueta de letras; escrevê-las ativa-o.
public static class LinkHints
{
    public const string Alphabet = "asdfghjklqwertyuiop";

    // Etiquetas sem prefixos ambíguos: todas com o mesmo comprimento (1 letra até 19 alvos, 2 letras até 361…).
    public static IReadOnlyList<string> Labels(int count)
    {
        if (count <= 0) return [];
        var length = 1;
        while (Math.Pow(Alphabet.Length, length) < count) length++;
        var labels = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var n = i;
            var chars = new char[length];
            for (var p = length - 1; p >= 0; p--)
            {
                chars[p] = Alphabet[n % Alphabet.Length];
                n /= Alphabet.Length;
            }
            labels.Add(new string(chars));
        }
        return labels;
    }

    public const string Script = """
        (() => {
          const ID = 'eteris-hints';
          const old = document.getElementById(ID);
          if (old) { old.remove(); return 'closed'; }
          const alphabet = 'asdfghjklqwertyuiop';
          const selector = 'a[href], button, input:not([type=hidden]), select, textarea, summary, [role=button], [role=link], [onclick], [tabindex]:not([tabindex="-1"])';
          const targets = [...document.querySelectorAll(selector)].filter(el => {
            const r = el.getBoundingClientRect();
            if (r.width < 4 || r.height < 4 || r.bottom < 0 || r.right < 0 || r.top > innerHeight || r.left > innerWidth) return false;
            const style = getComputedStyle(el);
            if (style.visibility === 'hidden' || style.display === 'none' || el.disabled) return false;
            const hit = document.elementFromPoint(Math.min(innerWidth - 1, r.left + Math.min(r.width / 2, 12)), Math.min(innerHeight - 1, r.top + r.height / 2));
            return hit && (hit === el || el.contains(hit) || hit.contains(el));
          }).slice(0, 360);
          if (!targets.length) return 'none';
          let length = 1;
          while (Math.pow(alphabet.length, length) < targets.length) length++;
          const label = i => { let s = ''; for (let p = 0; p < length; p++) { s = alphabet[i % alphabet.length] + s; i = Math.floor(i / alphabet.length); } return s; };

          const host = document.createElement('div');
          host.id = ID;
          host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none;';
          const root = host.attachShadow({ mode: 'open' });
          const sheet = new CSSStyleSheet();
          sheet.replaceSync(`.h { position: fixed; font: 700 11px/1 Consolas, monospace; letter-spacing: .04em; text-transform: uppercase;
            padding: 3px 5px; border-radius: 5px; color: #0b0d13; background: linear-gradient(135deg, #c4b5fd, #67e8f9);
            box-shadow: 0 2px 8px rgba(0,0,0,.35); border: 1px solid rgba(11,13,19,.25); }
            .h b { color: #8b5cf6; } .gone { display: none; }`);
          root.adoptedStyleSheets = [sheet];
          const hints = targets.map((el, i) => {
            const r = el.getBoundingClientRect();
            const tag = document.createElement('div');
            tag.className = 'h';
            tag.textContent = label(i);
            tag.style.left = Math.max(0, r.left - 4) + 'px';
            tag.style.top = Math.max(0, r.top - 4) + 'px';
            root.append(tag);
            return { el, text: label(i), tag };
          });
          document.documentElement.appendChild(host);

          let typed = '';
          const finish = () => { removeEventListener('keydown', key, true); host.remove(); };
          const key = e => {
            if (e.ctrlKey || e.altKey || e.metaKey) return;
            e.preventDefault(); e.stopImmediatePropagation();
            if (e.key === 'Escape') { finish(); return; }
            if (e.key === 'Backspace') typed = typed.slice(0, -1);
            else if (e.key.length === 1 && alphabet.includes(e.key.toLowerCase())) typed += e.key.toLowerCase();
            else return;
            let match = null, visible = 0;
            for (const h of hints) {
              const ok = h.text.startsWith(typed);
              h.tag.classList.toggle('gone', !ok);
              if (ok) {
                visible++;
                h.tag.textContent = '';
                const done = document.createElement('b'); done.textContent = h.text.slice(0, typed.length);
                h.tag.append(done, h.text.slice(typed.length));
                if (h.text === typed) match = h;
              }
            }
            if (match) {
              finish();
              const el = match.el;
              if (el.matches('input, textarea, select, [contenteditable]')) el.focus();
              else el.click();
            } else if (visible === 0) finish();
          };
          addEventListener('keydown', key, true);
          return 'shown:' + targets.length;
        })()
        """;
}
