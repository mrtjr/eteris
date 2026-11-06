namespace Eteris.Browser.Browser;

// Modo de leitura: extração do artigo feita na própria página (heurística de densidade de texto, à maneira
// do Readability) e apresentação numa camada isolada em Shadow DOM. Os estilos entram por adoptedStyleSheets
// e o DOM é criado com createElement, por isso funciona mesmo em sites com CSP ou Trusted Types restritos.
public static class ReaderScripts
{
    public static bool IsCandidate(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    // Igual ao isProbablyReaderable do Firefox: soma de √(comprimento − 140) dos parágrafos longos > 20.
    public const string Detect = """
        (() => {
          let score = 0;
          for (const p of document.querySelectorAll('p, pre, article div')) {
            if (p.closest('nav, footer, aside, form')) continue;
            const length = (p.textContent || '').trim().length;
            if (length < 140) continue;
            score += Math.sqrt(length - 140);
            if (score > 20) return true;
          }
          return false;
        })()
        """;

    public const string Toggle = """
        (() => {
          const ID = 'eteris-reader';
          const open = document.getElementById(ID);
          if (open) { open.remove(); document.documentElement.style.overflow = ''; return 'closed'; }

          const junk = /comment|footer|sidebar|nav|menu|share|social|related|promo|advert|cookie|newsletter|subscribe|banner/i;
          const scores = new Map();
          for (const p of document.querySelectorAll('p, pre, blockquote')) {
            const text = (p.innerText || '').trim();
            if (text.length < 40) continue;
            const score = 1 + Math.min(Math.floor(text.length / 100), 3) + text.split(',').length;
            let node = p.parentElement, level = 0;
            while (node && level < 3) {
              const bad = junk.test((node.className || '') + ' ' + (node.id || ''));
              const add = (bad ? -0.5 : 1) * score / (level === 0 ? 1 : level * 2);
              scores.set(node, (scores.get(node) || 0) + add);
              node = node.parentElement; level++;
            }
          }
          let best = null, bestScore = 0;
          for (const [node, score] of scores) {
            const total = (node.innerText || '').length || 1;
            const links = [...node.querySelectorAll('a')].reduce((n, a) => n + (a.innerText || '').length, 0);
            const adjusted = score * (1 - links / total);
            if (adjusted > bestScore) { bestScore = adjusted; best = node; }
          }
          if (!best) return 'none';

          const content = best.cloneNode(true);
          content.querySelectorAll('img').forEach(img => {
            const lazy = img.getAttribute('data-src') || img.getAttribute('data-lazy-src') || img.getAttribute('data-original');
            if (lazy) img.setAttribute('src', lazy);
            if (img.src) img.setAttribute('src', img.src);
          });
          content.querySelectorAll('a').forEach(a => { if (a.href) a.setAttribute('href', a.href); });
          const clutter = /(^|[\s_-])(infobox|navbox|ambox|metadata|noprint|hatnote|editsection|toc|reference|references|catlinks|share|social|related|advert|promo|newsletter|subscribe|breadcrumb)([\s_-]|$)/i;
          content.querySelectorAll('[class], [id]').forEach(n => {
            if (clutter.test(n.getAttribute('class') || '') || clutter.test(n.id || '')) n.remove();
          });
          content.querySelectorAll('script, style, noscript, iframe, form, button, input, select, textarea, nav, aside, footer, svg, video, audio, [aria-hidden="true"], [role="complementary"], [hidden]')
            .forEach(n => n.remove());
          content.querySelectorAll('div, section, ul, ol').forEach(n => {
            const text = (n.textContent || '').trim();
            const links = [...n.querySelectorAll('a')].reduce((x, a) => x + (a.textContent || '').length, 0);
            if (text.length < 240 && links / (text.length || 1) > 0.5) n.remove();
            else if (text.length === 0 && !n.querySelector('img')) n.remove();
          });
          content.querySelectorAll('*').forEach(n => {
            for (const attr of [...n.attributes]) if (!['href', 'src', 'alt', 'title'].includes(attr.name)) n.removeAttribute(attr.name);
          });
          content.querySelectorAll('a').forEach(a => { a.target = '_blank'; a.rel = 'noopener'; });

          const meta = name => (document.querySelector(`meta[property="${name}"], meta[name="${name}"]`) || {}).content || '';
          const heading = ((document.querySelector('h1') || {}).innerText || '').trim();
          const fullTitle = meta('og:title') || document.title;
          // «Fado – Wikipédia, a enciclopédia livre» → «Fado»: o h1 ganha quando está contido no título.
          const title = heading && (!fullTitle || fullTitle.includes(heading)) ? heading : (fullTitle || heading);
          const site = meta('og:site_name') || location.hostname.replace(/^www\./, '');
          const author = meta('author') || meta('article:author');
          content.querySelectorAll('h1').forEach(h => { if ((h.innerText || '').trim() === title.trim()) h.remove(); });
          const words = (content.textContent || '').trim().split(/\s+/).length;
          const minutes = Math.max(1, Math.round(words / 230));

          const css = `
            :host { all: initial; }
            .wrap { position: fixed; inset: 0; overflow-y: auto; z-index: 2147483647; background: var(--bg); color: var(--fg);
                    transition: background .25s, color .25s; font-family: var(--font); }
            .wrap.dark  { --bg: #12141b; --fg: #dfe2ea; --muted: #8b91a3; --line: #262a36; --link: #7dd3fc; --panel: #1b1e28; }
            .wrap.sepia { --bg: #f4ecd8; --fg: #3b3226; --muted: #8a7b63; --line: #e2d6b9; --link: #9a4d1b; --panel: #ebe1c7; }
            .wrap.light { --bg: #ffffff; --fg: #1f2328; --muted: #6b7280; --line: #e5e7eb; --link: #2563eb; --panel: #f3f4f6; }
            .wrap.serif { --font: Charter, "Iowan Old Style", Georgia, Cambria, serif; }
            .wrap.sans  { --font: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
            .bar { position: sticky; top: 0; display: flex; justify-content: center; gap: 6px; padding: 14px; z-index: 1;
                   background: linear-gradient(var(--bg) 60%, transparent); font: 13px "Segoe UI", system-ui, sans-serif; }
            .bar button { all: unset; cursor: pointer; padding: 7px 12px; border-radius: 10px; color: var(--muted);
                          background: var(--panel); border: 1px solid var(--line); }
            .bar button:hover { color: var(--fg); }
            .bar button.on { color: var(--fg); border-color: var(--muted); }
            .bar .gap { width: 14px; }
            .doc { max-width: 680px; margin: 10px auto 120px; padding: 0 28px; font-size: var(--size); line-height: 1.75; }
            .site { color: var(--muted); font: 600 12px "Segoe UI", system-ui, sans-serif; letter-spacing: .12em; text-transform: uppercase; margin: 22px 0 10px; }
            h1.title { font-size: 2.05em; line-height: 1.2; margin: 0 0 14px; font-weight: 700; letter-spacing: -.01em; }
            .meta { color: var(--muted); font: 13.5px "Segoe UI", system-ui, sans-serif; padding-bottom: 26px; margin-bottom: 30px; border-bottom: 1px solid var(--line); }
            .content img { max-width: 100%; height: auto; border-radius: 10px; margin: 14px 0; display: block; }
            .content a { color: var(--link); text-decoration: underline; text-underline-offset: 3px; }
            .content h2, .content h3 { line-height: 1.3; margin: 1.8em 0 .6em; }
            .content p { margin: 0 0 1.15em; }
            .content blockquote { margin: 1.4em 0; padding: .2em 0 .2em 1.1em; border-left: 3px solid var(--muted); color: var(--muted); font-style: italic; }
            .content pre { background: var(--panel); padding: 14px; border-radius: 10px; overflow-x: auto; font-size: .82em; line-height: 1.5; }
            .content figure { margin: 1.4em 0; }
            .content figcaption { color: var(--muted); font: 13px "Segoe UI", system-ui, sans-serif; }
          `;
          const host = document.createElement('div');
          host.id = ID;
          host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;';
          const root = host.attachShadow({ mode: 'open' });
          const sheet = new CSSStyleSheet();
          sheet.replaceSync(css);
          root.adoptedStyleSheets = [sheet];

          const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
          const wrap = el('div', 'wrap dark serif');
          let size = 20;
          wrap.style.setProperty('--size', size + 'px');

          const bar = el('div', 'bar');
          const button = (label, title, action) => { const b = el('button', null, label); b.title = title; b.addEventListener('click', action); bar.append(b); return b; };
          button('A−', 'Letra mais pequena', () => { size = Math.max(15, size - 1); wrap.style.setProperty('--size', size + 'px'); });
          button('A+', 'Letra maior', () => { size = Math.min(28, size + 1); wrap.style.setProperty('--size', size + 'px'); });
          bar.append(el('span', 'gap'));
          const themes = ['dark', 'sepia', 'light'].map((theme, i) => button(['Escuro', 'Sépia', 'Claro'][i], 'Tema ' + theme, () => {
            wrap.classList.remove('dark', 'sepia', 'light'); wrap.classList.add(theme);
            themes.forEach(t => t.classList.toggle('on', t === themes[i]));
          }));
          themes[0].classList.add('on');
          bar.append(el('span', 'gap'));
          const font = button('Sem serifa', 'Alternar tipo de letra', () => {
            const serif = wrap.classList.toggle('serif'); wrap.classList.toggle('sans', !serif);
            font.textContent = serif ? 'Sem serifa' : 'Com serifa';
          });
          bar.append(el('span', 'gap'));
          button('✕  Sair', 'Sair do modo de leitura (Esc ou F9)', () => close());

          const article = el('article', 'doc');
          article.append(el('p', 'site', site), el('h1', 'title', title),
            el('p', 'meta', (author ? author + ' · ' : '') + minutes + ' min de leitura'));
          const body = el('div', 'content');
          body.append(content);
          article.append(body);
          wrap.append(bar, article);
          root.append(wrap);

          const close = () => { host.remove(); document.documentElement.style.overflow = ''; removeEventListener('keydown', onKey, true); };
          const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
          addEventListener('keydown', onKey, true);
          document.documentElement.appendChild(host);
          document.documentElement.style.overflow = 'hidden';
          wrap.focus();
          return 'opened';
        })()
        """;
}
