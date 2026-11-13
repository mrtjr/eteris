namespace Eteris.Browser.Browser;

// Modo de leitura: extração do artigo feita na própria página (heurística de densidade de texto, à maneira
// do Readability) e apresentação numa camada isolada em Shadow DOM. Os estilos entram por adoptedStyleSheets
// e o DOM é criado com createElement, por isso funciona mesmo em sites com CSP ou Trusted Types restritos.
public static class ReaderScripts
{
    public const string ReportPrefix = "\u0001eteris-reader ";

    public static string Toggle => ToggleWith("dark", 20, true);

    public static string ToggleWith(string theme, int size, bool serif)
        => InjectReadability(ToggleTemplate
            .Replace("__THEME__", theme is "dark" or "sepia" or "light" ? theme : "dark")
            .Replace("__SIZE__", Math.Clamp(size, 15, 28).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__SERIF__", serif ? "true" : "false"));

    // Bibliotecas do Firefox (Mozilla, Apache-2.0), embutidas sem alterações em Browser/Vendor/readability.
    private static readonly Lazy<string> LazyReadability = new(() => Resource("Eteris.Browser.Readability.js"));
    private static readonly Lazy<string> LazyReaderable = new(() => Resource("Eteris.Browser.Readability-readerable.js"));
    private static string ReaderableSource => LazyReaderable.Value;

    private static string Resource(string name)
    {
        using var stream = typeof(ReaderScripts).Assembly.GetManifestResourceStream(name)!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Outros scripts que precisam do Readability (ex.: o cartão «Espreitar»): o script tem de começar por «(() => {».
    public static string WithReadability(string script) => InjectReadability(script);

    private static string InjectReadability(string script)
    {
        const string opening = "(() => {";
        var at = script.IndexOf(opening, StringComparison.Ordinal);
        return script[..(at + opening.Length)] + "\n" + LazyReadability.Value + "\n" + script[(at + opening.Length)..];
    }

    // Mensagem de consola do leitor com as preferências; qualquer outra coisa (ou valores fora do intervalo) é ignorada.
    public static (string Theme, int Size, bool Serif)? ParseReport(string? message)
    {
        if (message is null || !message.StartsWith(ReportPrefix, StringComparison.Ordinal)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message[ReportPrefix.Length..]);
            var root = doc.RootElement;
            var theme = root.GetProperty("theme").GetString();
            var size = root.GetProperty("size").GetInt32();
            var serif = root.GetProperty("serif").GetBoolean();
            if (theme is not ("dark" or "sepia" or "light") || size is < 15 or > 28) return null;
            return (theme, size, serif);
        }
        catch (Exception) { return null; }
    }

    // O leitor corre depois da página, por isso o seu aviso não pode levar o segredo do processo (a página leria-o). Só
    // vale da aba à vista e com o leitor aberto por ela (BrowserTab.ReaderOpen): uma página sem o leitor, ou uma aba
    // escondida, já não muda as preferências. Limite: se o leitor for fechado dentro da página (Esc), o Éteris só o
    // sabe na navegação seguinte; os valores continuam limitados a três temas, 15 a 28 e um booleano.
    public static bool AcceptsReport(bool tabIsActive, bool readerOpen) => tabIsActive && readerOpen;

    public static bool IsCandidate(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    // Deteção de artigos com o isProbablyReaderable do Firefox (Readability-readerable.js, Mozilla, Apache-2.0).
    public static string Detect => $$"""
        (() => {
          {{ReaderableSource}}
          try { return isProbablyReaderable(document); } catch (e) { return false; }
        })()
        """;

    private const string ToggleTemplate = """
        (() => {
          const ID = 'eteris-reader';
          const open = document.getElementById(ID);
          if (open) { if (window.speechSynthesis) speechSynthesis.cancel(); open.remove(); document.documentElement.style.overflow = ''; return 'closed'; }

          // Primeiro o Readability (o motor do modo de leitura do Firefox, testado em milhões de páginas). Se falhar
          // (por exemplo em sites com Trusted Types, que proíbem escrever HTML diretamente), usa-se a heurística própria.
          let extracted = null;
          try {
            if (typeof Readability === 'function') {
              // O Readability apaga as classes; estas ficam para a limpeza abaixo reconhecer caixas de navegação e afins.
              const keep = ['infobox', 'navbox', 'ambox', 'metadata', 'noprint', 'hatnote', 'editsection', 'toc', 'reference', 'references', 'catlinks', 'share', 'social', 'related', 'advert', 'promo', 'newsletter', 'subscribe', 'breadcrumb'];
              const parsed = new Readability(document.cloneNode(true), { serializer: el => el, classesToPreserve: keep }).parse();
              if (parsed && parsed.content && (parsed.textContent || '').trim().length > 200) extracted = parsed;
            }
          } catch (e) { extracted = null; }

          let best = null;
          if (!extracted) {
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
            let bestScore = 0;
            for (const [node, score] of scores) {
              const total = (node.innerText || '').length || 1;
              const links = [...node.querySelectorAll('a')].reduce((n, a) => n + (a.innerText || '').length, 0);
              const adjusted = score * (1 - links / total);
              if (adjusted > bestScore) { bestScore = adjusted; best = node; }
            }
            if (!best) return 'none';
          }

          const content = extracted ? extracted.content : best.cloneNode(true);
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
          // «HTML: Linguagem… | MDN» → o h1 ganha quando está contido no título (sem o nome do site).
          const baseTitle = (extracted && extracted.title) || fullTitle;
          const title = heading && (!baseTitle || baseTitle.includes(heading)) ? heading : (baseTitle || heading);
          const site = meta('og:site_name') || (extracted && extracted.siteName) || location.hostname.replace(/^www\./, '');
          const author = (extracted && extracted.byline) || meta('author') || meta('article:author');
          content.querySelectorAll('h1').forEach(h => { if ((h.innerText || '').trim() === title.trim()) h.remove(); });
          const words = (content.textContent || '').trim().split(/\s+/).length;
          const minutes = Math.max(1, Math.round(words / 230));

          // Sem barra horizontal: o texto parte palavras compridas, as tabelas e os blocos de código rolam por dentro, as
          // figuras e a média cabem na coluna. As barras são finas, sem setas, na cor do tema (nunca as propriedades
          // normalizadas da cor e da largura da barra, que desligam o ::-webkit-scrollbar), e «reduzir movimento» tira as
          // transições.
          const css = `
            :host { all: initial; }
            .wrap { position: fixed; inset: 0; overflow-y: auto; overflow-x: hidden; z-index: 2147483647; background: var(--bg); color: var(--fg);
                    transition: background .25s, color .25s; font-family: var(--font); }
            .wrap.dark  { --bg: #12141b; --fg: #dfe2ea; --muted: #8b91a3; --line: #262a36; --link: #7dd3fc; --panel: #1b1e28; --hl: rgba(125, 211, 252, .13); }
            .wrap.sepia { --bg: #f4ecd8; --fg: #3b3226; --muted: #8a7b63; --line: #e2d6b9; --link: #9a4d1b; --panel: #ebe1c7; --hl: rgba(154, 77, 27, .12); }
            .wrap.light { --bg: #ffffff; --fg: #1f2328; --muted: #6b7280; --line: #e5e7eb; --link: #2563eb; --panel: #f3f4f6; --hl: rgba(37, 99, 235, .1); }
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
            .content { overflow-wrap: anywhere; }
            .content img { max-width: 100%; height: auto; border-radius: 10px; margin: 14px 0; display: block; }
            .content table { display: block; max-width: 100%; overflow-x: auto; }
            .content figure, .content video, .content iframe, .content svg, .content canvas { max-width: 100%; height: auto; }
            .content a { color: var(--link); text-decoration: underline; text-underline-offset: 3px; }
            .content h2, .content h3 { line-height: 1.3; margin: 1.8em 0 .6em; }
            .content p { margin: 0 0 1.15em; }
            .content blockquote { margin: 1.4em 0; padding: .2em 0 .2em 1.1em; border-left: 3px solid var(--muted); color: var(--muted); font-style: italic; }
            .content pre { background: var(--panel); padding: 14px; border-radius: 10px; overflow-x: auto; font-size: .82em; line-height: 1.5; }
            .content figure { margin: 1.4em 0; }
            .content figcaption { color: var(--muted); font: 13px "Segoe UI", system-ui, sans-serif; }
            .speaking { background: var(--hl); box-shadow: 0 0 0 8px var(--hl); border-radius: 4px; transition: background .3s, box-shadow .3s; }
            .listening .content p, .listening .content li, .listening .content h2, .listening .content h3, .listening .content blockquote { cursor: pointer; }
            .bar button.live { color: var(--fg); border-color: var(--link); }
            ::-webkit-scrollbar { width: 10px; height: 10px; background: transparent; }
            ::-webkit-scrollbar-button { display: none; }
            ::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }
            ::-webkit-scrollbar-thumb { background: var(--muted); border-radius: 999px; border: 2px solid transparent; background-clip: padding-box; }
            ::-webkit-scrollbar-thumb:hover { background-color: var(--fg); }
            @media (prefers-reduced-motion: reduce) {
              *, *::before, *::after { animation-duration: .01ms !important; transition-duration: .01ms !important; scroll-behavior: auto !important; }
            }
          `;
          const host = document.createElement('div');
          host.id = ID;
          host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;';
          const root = host.attachShadow({ mode: 'open' });
          const sheet = new CSSStyleSheet();
          sheet.replaceSync(css);
          root.adoptedStyleSheets = [sheet];

          const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
          // Preferências lembradas (tema, tamanho, tipo de letra): vêm do Éteris e cada mudança é-lhe comunicada.
          let theme = '__THEME__';
          const wrap = el('div', 'wrap ' + theme + (__SERIF__ ? ' serif' : ' sans'));
          let size = __SIZE__;
          wrap.style.setProperty('--size', size + 'px');
          const report = () => console.debug('\u0001eteris-reader ' + JSON.stringify({ theme, size, serif: wrap.classList.contains('serif') }));

          const bar = el('div', 'bar');
          const button = (label, title, action) => { const b = el('button', null, label); b.title = title; b.addEventListener('click', action); bar.append(b); return b; };
          button('A−', 'Letra mais pequena', () => { size = Math.max(15, size - 1); wrap.style.setProperty('--size', size + 'px'); report(); });
          button('A+', 'Letra maior', () => { size = Math.min(28, size + 1); wrap.style.setProperty('--size', size + 'px'); report(); });
          bar.append(el('span', 'gap'));
          const names = ['dark', 'sepia', 'light'];
          const themes = names.map((name, i) => button(['Escuro', 'Sépia', 'Claro'][i], 'Tema ' + name, () => {
            wrap.classList.remove('dark', 'sepia', 'light'); wrap.classList.add(name); theme = name;
            themes.forEach(t => t.classList.toggle('on', t === themes[i]));
            report();
          }));
          themes[Math.max(0, names.indexOf(theme))].classList.add('on');
          bar.append(el('span', 'gap'));
          const font = button(wrap.classList.contains('serif') ? 'Sem serifa' : 'Com serifa', 'Alternar tipo de letra', () => {
            const serif = wrap.classList.toggle('serif'); wrap.classList.toggle('sans', !serif);
            font.textContent = serif ? 'Sem serifa' : 'Com serifa';
            report();
          });
          bar.append(el('span', 'gap'));

          // «Ouvir»: leitura em voz alta parágrafo a parágrafo, só com vozes instaladas neste computador
          // (localService) — o texto nunca sai da máquina. Clicar num parágrafo passa a ler a partir dele.
          const synth = window.speechSynthesis;
          let stopSpeech = () => {};
          if (synth) {
            const lang = (document.documentElement.lang || navigator.language || 'pt').toLowerCase();
            let queue = [], index = 0, state = 'idle', rate = 1, current = null;
            const rates = [1, 1.25, 1.5, 0.85];
            const pickVoice = () => {
              const voices = synth.getVoices().filter(v => v.localService);
              const base = lang.split('-')[0];
              return voices.find(v => v.lang.toLowerCase() === lang) || voices.find(v => v.lang.toLowerCase().startsWith(base)) || null;
            };
            const blocks = () => [article.querySelector('h1.title'), ...body.querySelectorAll('h2, h3, p, li, blockquote, figcaption')]
              .filter(n => n && (n.innerText || '').trim().length > 1 && !(n.parentElement && n.parentElement.closest('p, li, blockquote, figcaption')));
            const mark = node => {
              article.querySelectorAll('.speaking').forEach(n => n.classList.remove('speaking'));
              if (!node) return;
              node.classList.add('speaking');
              const still = matchMedia('(prefers-reduced-motion: reduce)').matches;
              node.scrollIntoView({ block: 'center', behavior: still ? 'auto' : 'smooth' });
            };
            const paint = () => {
              listen.textContent = state === 'playing' ? '❚❚  Pausar' : state === 'paused' ? '▶  Continuar' : '▶  Ouvir';
              listen.classList.toggle('live', state !== 'idle');
              speed.style.display = state === 'idle' ? 'none' : '';
              wrap.classList.toggle('listening', state !== 'idle');
            };
            const speakFrom = i => {
              synth.cancel();
              if (!queue.length) queue = blocks();
              index = i; state = 'playing'; paint();
              const next = () => {
                if (state !== 'playing') return;
                if (index >= queue.length) { state = 'idle'; current = null; mark(null); paint(); return; }
                const node = queue[index];
                mark(node);
                const u = new SpeechSynthesisUtterance((node.innerText || '').trim());
                const voice = pickVoice();
                if (voice) { u.voice = voice; u.lang = voice.lang; } else u.lang = lang;
                u.rate = rate;
                u.onend = () => { if (current === u) { index++; next(); } };
                u.onerror = e => { if (current === u && e.error !== 'interrupted' && e.error !== 'canceled') { index++; next(); } };
                current = u;
                synth.speak(u);
              };
              next();
            };
            const pause = () => { state = 'paused'; current = null; synth.cancel(); paint(); };
            stopSpeech = () => { state = 'idle'; current = null; synth.cancel(); };
            const listen = button('▶  Ouvir', 'Ler o artigo em voz alta (vozes deste computador)', () => {
              if (state === 'playing') pause(); else speakFrom(state === 'paused' ? index : 0);
            });
            const speed = button('1×', 'Velocidade da leitura', () => {
              rate = rates[(rates.indexOf(rate) + 1) % rates.length];
              speed.textContent = String(rate).replace('.', ',') + '×';
              if (state === 'playing') speakFrom(index);
            });
            wrap.addEventListener('click', e => {
              if (state === 'idle' || e.target.closest('a')) return;
              const node = queue.find(n => n.contains(e.target));
              if (node) speakFrom(queue.indexOf(node));
            });
            paint();
            bar.append(el('span', 'gap'));
          }
          button('✕  Sair', 'Sair do modo de leitura (Esc ou F9)', () => close());

          const article = el('article', 'doc');
          article.append(el('p', 'site', site), el('h1', 'title', title),
            el('p', 'meta', (author ? author + ' · ' : '') + minutes + ' min de leitura'));
          const body = el('div', 'content');
          body.append(content);
          article.append(body);
          wrap.append(bar, article);
          root.append(wrap);

          const close = () => { stopSpeech(); host.remove(); document.documentElement.style.overflow = ''; removeEventListener('keydown', onKey, true); };
          const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
          addEventListener('keydown', onKey, true);
          document.documentElement.appendChild(host);
          document.documentElement.style.overflow = 'hidden';
          wrap.focus();
          return 'opened';
        })()
        """;
}
