using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Browser;

// O que o cartão «Espreitar» mostra de um link: título, site, descrição, imagem (og:image), tempo de leitura e os
// primeiros parágrafos do artigo. FullParagraphs é o artigo inteiro que «Ler tudo aqui» mostra (sem ele, o resumo).
public sealed record LinkPreviewInfo(string Url, string Title, string Site, string? Description, string? ImageUrl,
    IReadOnlyList<string> Paragraphs, int Words, string? Byline = null, IReadOnlyList<string>? FullParagraphs = null)
{
    public IReadOnlyList<string> FullParagraphs { get; init; } = FullParagraphs ?? Paragraphs;

    // Há mais do que o resumo mostra: mais parágrafos, ou o último do resumo foi cortado.
    public bool HasMore => !FullParagraphs.SequenceEqual(Paragraphs);

    public int ReadingMinutes => LinkPreview.ReadingMinutes(Words);

    // «3 min de leitura»; vazio quando não há texto que chegue para contar.
    public string ReadingTime => ReadingMinutes == 0 ? string.Empty : $"{ReadingMinutes} min de leitura";
}

// O que uma tecla faz no cartão «Espreitar».
public enum GlanceAction { None, Close, Open, OpenSideBySide, ReadLater, Expand }

// Rolagem do cartão pelo teclado (o foco está na camada, não no ScrollViewer).
public enum GlanceScroll { None, LineUp, LineDown, PageUp, PageDown, Top, Bottom }

// Metadados tirados do HTML de uma página (etiquetas <meta> Open Graph/Twitter, <title>, <p>): lógica pura, sem motor.
public sealed record PageMetadata(string? Title, string? Description, string? ImageUrl, string? SiteName, IReadOnlyList<string> Paragraphs);

// Espreitar um link: a página é carregada numa aba escondida (sem JavaScript, sem cookies) e este script, corrido
// pelo DevTools, devolve o cabeçalho e o texto do artigo (Readability do Firefox) em JSON; o resto é feito aqui.
public static partial class LinkPreview
{
    public const int WordsPerMinute = 230;
    public const int MaxParagraphs = 3;
    public const int MaxExcerptChars = 700;
    public const int MinParagraphChars = 40;
    // A descrição (og:description) há sites que lá metem o artigo inteiro: o cartão mostra só o princípio.
    public const int MaxDescriptionChars = 300;
    // «Ler tudo aqui»: o artigo inteiro, até este total (o mesmo limite do script na aba escondida).
    public const int MaxFullChars = 20000;

    // Resultado do script na aba escondida: cabeçalho (meta) + artigo (Readability) + texto de recurso.
    public static string Script => ReaderScripts.WithReadability(ScriptTemplate.Replace("__COLLECT_PARAS__", CollectParasScript, StringComparison.Ordinal));

    // Os parágrafos de prosa do artigo: tira do conteúdo o que não é texto corrido (notas de rodapé, ligações «editar»,
    // tabelas e caixas de informação, figuras, notas) e lê até 60 <p>, cada um inteiro e com os espaços juntos, até
    // MaxFullChars no total. Função à parte para se testar no Node, com uma árvore falsa.
    internal const string CollectParasScript = """
        const collectParas = (root) => {
          const paras = [];
          if (!root || typeof root.querySelectorAll !== 'function') return paras;
          for (const node of Array.from(root.querySelectorAll('sup, .mw-editsection, table, figure, figcaption, .infobox, .navbox, style, script, [role=note]'))) {
            if (node && typeof node.remove === 'function') node.remove();
          }
          let total = 0;
          for (const p of Array.from(root.querySelectorAll('p'))) {
            const text = (p.textContent || '').replace(/\s+/g, ' ').trim();
            if (text.length < 40) continue;
            if (total + text.length > 20000) break;
            paras.push(text);
            total += text.length;
            if (paras.length >= 60) break;
          }
          return paras;
        };
        """;

    // O Readability devolve o elemento do conteúdo (serializer: el => el) e guarda as classes que collectParas procura
    // (sem isso tira-as todas). Sem Readability, os parágrafos vêm de uma cópia do <body>.
    private const string ScriptTemplate = """
        (() => {
          const cap = (text, max) => (text || '').length > max ? (text || '').slice(0, max) : (text || '');
          __COLLECT_PARAS__
          let article = null;
          try {
            if (typeof Readability === 'function') {
              const parsed = new Readability(document.cloneNode(true), {
                serializer: el => el,
                classesToPreserve: ['mw-editsection', 'infobox', 'navbox'],
              }).parse();
              if (parsed && (parsed.textContent || '').trim().length > 0) article = parsed;
            }
          } catch (e) { article = null; }
          let paras = [];
          try {
            paras = collectParas(article && article.content && typeof article.content === 'object'
              ? article.content
              : (document.body ? document.body.cloneNode(true) : null));
          } catch (e) { paras = []; }
          const fallback = () => {
            const bits = [];
            for (const p of document.querySelectorAll('p')) {
              const text = (p.innerText || p.textContent || '').trim();
              if (text.length >= 40) bits.push(text);
              if (bits.length >= 12) break;
            }
            return bits.join('\n');
          };
          return JSON.stringify({
            head: cap(document.head ? document.head.outerHTML : '', 120000),
            title: article ? article.title : '',
            byline: article ? article.byline : '',
            site: article ? article.siteName : '',
            text: cap(article ? article.textContent : fallback(), 6000),
            words: article ? (article.textContent || '').trim().split(/\s+/).filter(Boolean).length : 0,
            paras: paras,
            excerpt: cap(article ? article.excerpt : '', 2000),
          });
        })()
        """;

    // Teclado no cartão: Esc fecha, Enter abre, Shift+Enter abre ao lado, Ctrl+Enter guarda para ler mais tarde
    // (na janela privada não há «Ler mais tarde») e E mostra ou recolhe o artigo inteiro («Ler tudo aqui»). Os botões
    // não recebem foco, por isso estas são as únicas teclas.
    public static GlanceAction ActionFor(string key, bool shift, bool ctrl, bool isPrivate) => key switch
    {
        "Escape" => GlanceAction.Close,
        "E" when !shift && !ctrl => GlanceAction.Expand,
        "Return" or "Enter" when ctrl && !shift => isPrivate ? GlanceAction.None : GlanceAction.ReadLater,
        "Return" or "Enter" when shift && !ctrl => GlanceAction.OpenSideBySide,
        "Return" or "Enter" when !shift && !ctrl => GlanceAction.Open,
        _ => GlanceAction.None,
    };

    // Teclas de rolagem do cartão: setas, Page Up/Down, Espaço (Shift+Espaço sobe), Home e End.
    public static GlanceScroll ScrollFor(string key, bool shift) => key switch
    {
        "Up" => GlanceScroll.LineUp,
        "Down" => GlanceScroll.LineDown,
        "PageUp" or "Prior" => GlanceScroll.PageUp,
        "PageDown" or "Next" => GlanceScroll.PageDown,
        "Space" => shift ? GlanceScroll.PageUp : GlanceScroll.PageDown,
        "Home" => GlanceScroll.Top,
        "End" => GlanceScroll.Bottom,
        _ => GlanceScroll.None,
    };

    // A aba escondida recusou uma transferência: o link é um ficheiro, não uma página.
    public const string FileStatus = "Este link é um ficheiro, não uma página — «Abrir» transfere-o";

    // Página lida, mas sem texto corrido nem descrição para resumir.
    public const string NoSummaryStatus = "Sem resumo para esta página — «Abrir» mostra-a";

    // O botão do rodapé que mostra o artigo inteiro no cartão (tecla E) e volta ao resumo.
    public static string ExpandLabel(bool expanded) => expanded ? "Mostrar menos" : "Ler tudo aqui";

    // A dica do mesmo botão (e o texto de ajuda que o leitor de ecrã tira dela).
    public static string ExpandTip(bool expanded) => expanded ? "Voltar ao resumo (E)" : "Mostrar o artigo inteiro aqui (E)";

    // A página que o cartão descreve: o endereço confirmado da moldura principal quando é uma página web (depois de um
    // redirecionamento, o destino, não o encurtador nem o http do link); senão (página de erro, about:, data:) o link.
    public static string PageUrl(string link, string? loaded)
        => loaded is not null && IsWeb(loaded) && Uri.TryCreate(loaded, UriKind.Absolute, out _) ? loaded : link;

    // Sem favicon na cache, o cartão mostra a 1.ª letra (ou algarismo) do site num círculo.
    public static string Monogram(string? site)
    {
        foreach (var rune in (site ?? string.Empty).EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) return rune.ToString().ToUpper(CultureInfo.CurrentCulture);
        return "?";
    }

    // Páginas onde o Alt+clique espreita: as da web e as internas (nova aba, favoritos, histórico), que têm links
    // web e onde, sem o script, o Chromium transferia o destino.
    public static bool WantsAltClick(string? url)
        => url is not null && (IsWeb(url) || url.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase));

    // Uma transferência que começa só com o Alt premido é o Alt+clique nativo do Chromium num link que o script não
    // apanhou (um iframe de outra origem vive noutro processo e não recebe os scripts do Éteris): não transfere,
    // espreita. Só links web.
    public static bool DivertsDownload(bool altOnlyHeld, string? url) => altOnlyHeld && url is not null && IsWeb(url);

    // O aviso do Alt+clique leva o segredo do processo (o mesmo dos cliques, PopupPolicy.ActivationNonce): qualquer
    // página escreve na consola, mas só o nosso script (registado antes dos da página, em modo estrito) o conhece.
    // Sem ele, uma página abria o cartão «Espreitar» sozinha a cada Alt premido (NAV-05).
    public static string ReportPrefix { get; } = "\u0001eteris-glance " + PopupPolicy.ActivationNonce + " ";

    // O aviso do Alt+clique só conta até este tempo depois de o Alt ter sido premido (ver AltTap.TakeGesture).
    public static readonly TimeSpan GestureWindow = TimeSpan.FromSeconds(3);

    // Imagem do cartão (og:image), pedida pelo motor: só respostas de sucesso, de imagem, até 4 MB.
    public const int MaxImageBytes = 4 * 1024 * 1024;

    public static bool AcceptsImage(int statusCode, string? mimeType, long length)
        => statusCode is >= 200 and <= 299 && length > 0 && length <= MaxImageBytes
           && (mimeType is null || mimeType.Length == 0 || mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

    // Alt+clique num link (botão esquerdo, só Alt): a página não navega nem transfere (é o que o Chromium faria com
    // Alt) e o Éteris recebe o endereço para o espreitar. Corre antes dos scripts da página, em captura na janela,
    // por isso a página não o consegue calar, e em modo estrito (sem .caller), por isso não lhe entrega o segredo.
    // Só links http(s) com o clique de verdade (isTrusted).
    public static string AltClickScript { get; } = AltClickScriptFor(PopupPolicy.ActivationNonce);

    internal static string AltClickScriptFor(string nonce) => AltClickTemplate.Replace("__NONCE__", nonce, StringComparison.Ordinal);

    private const string AltClickTemplate = """
        (() => {
          'use strict';
          const debug = console.debug.bind(console);
          const prefix = '\u0001eteris-glance __NONCE__ ';
          addEventListener('click', e => {
            if (!e.isTrusted || !e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
            for (const node of (e.composedPath ? e.composedPath() : [e.target])) {
              if (!(node instanceof Element)) continue;
              if ((node.tagName === 'A' || node.tagName === 'AREA') && node.href && /^https?:/i.test(node.href)) {
                e.preventDefault();
                e.stopImmediatePropagation();
                debug(prefix + node.href);
                return;
              }
            }
          }, true);
        })();
        """;

    // Mensagem de consola do script acima: prefixo + endereço do link. Qualquer outra coisa é ignorada.
    public static bool IsReport(string? message, out string url)
    {
        url = string.Empty;
        if (message is null || !message.StartsWith(ReportPrefix, StringComparison.Ordinal)) return false;
        var candidate = message[ReportPrefix.Length..].Trim();
        if (!IsWeb(candidate) || !Uri.TryCreate(candidate, UriKind.Absolute, out _)) return false;
        url = candidate;
        return true;
    }

    // JSON do script → o que o cartão mostra. null quando o JSON não presta.
    // Parágrafos: os de «paras» que são prosa (IsProse); senão as linhas do «text» que são prosa (o recurso de antes,
    // sem Readability ou de um script antigo); senão os de «paras» sem o filtro (línguas e páginas onde nada passa, para
    // não ficar pior do que antes); por fim os <p> do HTML. O cartão mostra os primeiros (MaxParagraphs,
    // MaxExcerptChars); FullParagraphs leva todos, inteiros, até MaxFullChars.
    public static LinkPreviewInfo? Parse(string? json, string url)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var head = Text(root, "head");
            var meta = FromHtml(head, url);
            var text = Text(root, "text");
            var host = HostOf(url);
            var articleTitle = Clean(Text(root, "title"));
            var articleSite = Clean(Text(root, "site"));
            var (title, tail) = CleanTitle(Pick(meta.Title, articleTitle) ?? host, meta.SiteName ?? articleSite, host);
            var site = ShortSite(meta.SiteName, tail, articleSite, host);

            var paras = Lines(Strings(root, "paras")).ToList();
            var candidates = paras.Where(IsProse).ToList();
            if (candidates.Count == 0) candidates = Lines(text.Split('\n')).Where(IsProse).ToList();
            if (candidates.Count == 0) candidates = paras;
            if (candidates.Count == 0) candidates = [.. meta.Paragraphs];
            var paragraphs = Excerpt(candidates, MaxParagraphs, MaxExcerptChars);
            var full = Excerpt(candidates, int.MaxValue, MaxFullChars, cutLast: false);

            var words = root.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.Number ? (int)w.GetDouble() : 0;
            if (words == 0) words = CountWords(text);
            if (words == 0) words = CountWords(string.Join(' ', full));
            // A descrição: a do cabeçalho; sem ela, o excerto do Readability, se for prosa.
            var excerpt = Clean(Text(root, "excerpt"));
            var description = meta.Description ?? (IsProse(excerpt) ? excerpt : null);
            if (description is { Length: > MaxDescriptionChars } longDescription) description = Truncate(longDescription, MaxDescriptionChars);
            // Uma descrição igual ao início do artigo repete-se: fica só o artigo.
            if (description is not null && full.Count > 0 && RepeatsStart(full[0], description)) description = null;
            return new LinkPreviewInfo(url, title, site, description, meta.ImageUrl, paragraphs, words, Clean(Text(root, "byline")), full);
        }
        catch (JsonException) { return null; }
    }

    // A descrição é o princípio do parágrafo, comparados pelas palavras (sem acentos, maiúsculas nem pontuação) e sem
    // as chamadas de nota («[1]», «[nota 2]»): o excerto do Readability é o 1.º parágrafo ainda com elas, e o
    // collectParas tira-as.
    private static bool RepeatsStart(string paragraph, string description)
    {
        var start = string.Join(' ', Words(NoteMark().Replace(description, " ")));
        return start.Length > 0 && string.Join(' ', Words(NoteMark().Replace(paragraph, " "))).StartsWith(start, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\[[^\[\]]{1,24}\]")]
    private static partial Regex NoteMark();

    // Uma linha é prosa (e não restos da estrutura da página: coordenadas, ligações «editar», menus, tabelas achatadas)
    // quando cumpre tudo isto: pelo menos MinParagraphChars e 6 palavras (a regra das palavras não vale nas escritas sem
    // espaços entre palavras, como o chinês e o japonês); letras em pelo menos 60% dos carateres que não são espaço;
    // algarismos e símbolos (o que não é letra nem pontuação de texto corrido: «°», «′», «·», «|»…) em no máximo 25%; no
    // máximo uma «palavra colada» (minúscula seguida de maiúscula dentro de uma palavra comprida, IsGlued); e acaba em
    // pontuação final ou tem pelo menos 120 carateres.
    public static bool IsProse(string? line)
    {
        var text = Clean(line);
        if (text is null || text.Length < MinParagraphChars) return false;
        int nonSpace = 0, letters = 0, unspaced = 0, digitsAndSymbols = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            nonSpace++;
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetter(rune) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                letters++;
                if (IsUnspacedScript(rune.Value)) unspaced++;
            }
            else if (Rune.IsDigit(rune) || !rune.IsBmp || !ProsePunctuation.Contains((char)rune.Value)) digitsAndSymbols++;
        }
        var words = text.Split(' ');
        var unspacedText = letters > 0 && unspaced * 2 >= letters;
        if (!unspacedText && words.Length < 6) return false;
        if (letters < nonSpace * 0.6) return false;
        if (digitsAndSymbols > nonSpace * 0.25) return false;
        if (words.Count(IsGlued) > 1) return false;
        return FinalPunctuation.Contains(text[^1]) || text.Length >= 120;
    }

    // Pontuação de texto corrido: não conta como símbolo.
    private const string ProsePunctuation = ".,;:!?…'\"«»“”‘’„()[]-–—/。、，！？：；「」『』（）";
    private const string FinalPunctuation = ".!?…:»\"”)。！？";

    // Uma minúscula seguida de uma maiúscula dentro de uma sequência de pelo menos MinGluedLetters letras seguidas
    // («InícioHistória», «LFuncionamentoEstatuto»): duas palavras da estrutura coladas. As marcas em CamelCase são curtas
    // («WhatsApp», «TikTok», «iPhone», «JavaScript», «PlayStation») e não contam, nem juntas por pontuação
    // («WhatsApp/TikTok», «JavaScript-based»): duas delas no mesmo parágrafo tiravam-no do resumo e de «Ler tudo aqui».
    private static bool IsGlued(string word)
    {
        foreach (Match run in LetterRun().Matches(word))
        {
            if (run.Length < MinGluedLetters) continue;
            for (var i = 1; i < run.Length; i++)
                if (char.IsLower(run.Value[i - 1]) && char.IsUpper(run.Value[i])) return true;
        }
        return false;
    }

    private const int MinGluedLetters = 12;

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex LetterRun();

    // Escritas sem espaços entre palavras: chinês (Han), japonês (hiragana, katakana), tailandês, laosiano, khmer, birmanês.
    private static bool IsUnspacedScript(int c)
        => c is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0x20000 and <= 0x3134F) or (>= 0x0E00 and <= 0x0EFF) or (>= 0x1780 and <= 0x17FF) or (>= 0x1000 and <= 0x109F);

    // O título sem o nome do site (« – Wikipédia, a enciclopédia livre», «PÚBLICO | »). Corta-se o último segmento ou,
    // se esse não for o site, o 1.º, quando é o nome do site (NamesSite): começa pelo nome do site ou pela marca do
    // anfitrião (o nome antes do sufixo público: «wikipedia», «publico», «bbc»), sem acentos nem maiúsculas e em palavras
    // inteiras, e pouco mais tem. Um segmento que só menciona o site («Ensino público», «Crossing the Atlantic», «Time to
    // act») é título. Nunca fica vazio. Tail é o segmento cortado.
    public static (string Title, string? Tail) CleanTitle(string title, string? siteName, string host)
    {
        var original = Clean(title) ?? string.Empty;
        int first = -1, firstLength = 0, last = -1, lastLength = 0;
        foreach (var separator in TitleSeparators)
        {
            var i = original.IndexOf(separator, StringComparison.Ordinal);
            if (i >= 0 && (first < 0 || i < first)) (first, firstLength) = (i, separator.Length);
            var j = original.LastIndexOf(separator, StringComparison.Ordinal);
            if (j > last) (last, lastLength) = (j, separator.Length);
        }
        if (first < 0) return (original, null);
        var names = new[] { Words(siteName), Words(Brand(host)) }.Where(n => string.Concat(n).Length >= 2).ToArray();
        var lead = original[..first].Trim();
        var end = original[(last + lastLength)..].Trim();
        if (NamesSite(end, names) && original[..last].Trim() is { Length: > 0 } kept) return (kept, end);
        if (NamesSite(lead, names) && original[(first + firstLength)..].Trim() is { Length: > 0 } rest) return (rest, lead);
        return (original, null);
    }

    private static readonly string[] TitleSeparators = [" – ", " — ", " | ", " - ", " · ", " :: "];

    // O segmento é o nome do site: as primeiras palavras formam o nome ou a marca («Blog do Zé» → «blogdoze», «El País» →
    // «elpais») e, até à 1.ª vírgula, sobra no máximo uma palavra («Wikipédia, a enciclopédia livre», «BBC News»,
    // «PÚBLICO»); ou o segmento inteiro é o nome («Wikimedia Foundation, Inc.»).
    private static bool NamesSite(string segment, string[][] names)
    {
        var words = Words(segment);
        var beforeComma = Words(segment.Split(',')[0]);
        foreach (var name in names)
        {
            var wanted = string.Concat(name);
            if (WordsAfter(words, wanted) == 0 || WordsAfter(beforeComma, wanted) is 0 or 1) return true;
        }
        return false;
    }

    // Quantas palavras sobram depois de as primeiras formarem o nome; -1 quando não o formam.
    private static int WordsAfter(string[] words, string wanted)
    {
        var joined = string.Empty;
        for (var i = 0; i < words.Length && joined.Length < wanted.Length; i++)
        {
            joined += words[i];
            if (joined == wanted) return words.Length - i - 1;
        }
        return -1;
    }

    // Palavras sem acentos nem maiúsculas, só com letras e algarismos.
    private static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // A marca do anfitrião: o 1.º rótulo do domínio registável («pt.wikipedia.org» → «wikipedia», «www.bbc.co.uk» → «bbc»).
    private static string Brand(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) is not UriHostNameType.Dns) return string.Empty;
        var site = PublicSuffix.RegistrableDomain(host);
        var dot = site.IndexOf('.');
        return dot > 0 ? site[..dot] : site;
    }

    // O nome curto do site, pela ordem: og:site_name ou application-name (até 30 carateres); o segmento cortado do título
    // até à 1.ª vírgula («Wikipédia, a enciclopédia livre» → «Wikipédia», também até 30); o siteName do Readability, se
    // não for o de uma entidade jurídica («Wikimedia Foundation, Inc.»); o anfitrião sem «www.».
    public static string ShortSite(string? ogSiteName, string? titleTail, string? readabilitySite, string host)
    {
        if (Clean(ogSiteName) is { Length: <= MaxSiteChars } og) return og;
        if (Clean(titleTail?.Split(',')[0]) is { Length: <= MaxSiteChars } fromTitle) return fromTitle;
        if (Clean(readabilitySite) is { } article && !LegalEntity().IsMatch(article)) return article;
        host = host.Trim();
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    private const int MaxSiteChars = 30;

    [GeneratedRegex(@"\b(Inc|Ltd|LLC|Lda|S\.?A|GmbH|Foundation|Corporation|Corp)\b")]
    private static partial Regex LegalEntity();

    // Metadados de uma página a partir do HTML (ou só do <head>): Open Graph, Twitter Cards, <title>, meta description,
    // e os primeiros <p> como texto de recurso. Os endereços relativos ficam absolutos em relação à página.
    public static PageMetadata FromHtml(string? html, string url)
    {
        html ??= string.Empty;
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in MetaTag().Matches(html))
        {
            var attributes = Attributes(tag.Groups[1].Value);
            var key = attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name");
            if (key is null || !attributes.TryGetValue("content", out var content)) continue;
            // A primeira etiqueta ganha (o og:image principal vem antes das variantes).
            meta.TryAdd(key.Trim(), WebUtility.HtmlDecode(content));
        }
        var title = Pick(meta.GetValueOrDefault("og:title"), meta.GetValueOrDefault("twitter:title"),
            TitleTag().Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value) : null);
        var description = Pick(meta.GetValueOrDefault("og:description"), meta.GetValueOrDefault("twitter:description"),
            meta.GetValueOrDefault("description"));
        var image = Pick(meta.GetValueOrDefault("og:image:secure_url"), meta.GetValueOrDefault("og:image"), meta.GetValueOrDefault("twitter:image"),
            meta.GetValueOrDefault("twitter:image:src"), LinkImage(html));
        var site = Pick(meta.GetValueOrDefault("og:site_name"), meta.GetValueOrDefault("application-name"));
        var paragraphs = Paragraphs(string.Join('\n', ParagraphTag().Matches(html).Select(m => WebUtility.HtmlDecode(StripTags(m.Groups[1].Value)))));
        return new PageMetadata(title, description, AbsoluteUrl(image, url), site, paragraphs);
    }

    // Os primeiros parágrafos legíveis do texto: linhas com pelo menos MinParagraphChars, até MaxParagraphs, num total
    // de MaxExcerptChars (o último é cortado numa palavra, com reticências).
    public static IReadOnlyList<string> Paragraphs(string? text, int max = MaxParagraphs, int maxChars = MaxExcerptChars)
        => string.IsNullOrWhiteSpace(text) ? [] : Excerpt(Lines(text.Split('\n')), max, maxChars);

    // Linhas limpas (espaços juntos) com pelo menos MinParagraphChars.
    private static IEnumerable<string> Lines(IEnumerable<string> raw)
        => raw.Select(Clean).OfType<string>().Where(line => line.Length >= MinParagraphChars);

    // Até max linhas num total de maxChars; a que já não cabe inteira é cortada numa palavra (cutLast, ou quando é a
    // primeira) ou fica de fora.
    private static List<string> Excerpt(IEnumerable<string> lines, int max, int maxChars, bool cutLast = true)
    {
        var result = new List<string>();
        var budget = maxChars;
        foreach (var line in lines)
        {
            if (result.Count >= max) break;
            if (line.Length > budget)
            {
                if ((cutLast || result.Count == 0) && budget >= MinParagraphChars) result.Add(Truncate(line, budget));
                break;
            }
            result.Add(line);
            budget -= line.Length;
        }
        return result;
    }

    public static int ReadingMinutes(int words) => words <= 0 ? 0 : Math.Max(1, (int)Math.Round(words / (double)WordsPerMinute));

    public static int CountWords(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // Só endereços http(s) absolutos (os relativos resolvem-se contra a página); «data:» e afins ficam de fora.
    public static string? AbsoluteUrl(string? candidate, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        candidate = candidate.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var page)) return IsWeb(candidate) ? candidate : null;
        if (!Uri.TryCreate(page, candidate, out var resolved)) return null;
        return resolved.Scheme is "http" or "https" ? resolved.AbsoluteUri : null;
    }

    public static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
            : url;

    private static bool IsWeb(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string line, int budget)
    {
        var cut = line.LastIndexOf(' ', Math.Min(budget, line.Length - 1));
        if (cut < MinParagraphChars) cut = budget;
        return line[..cut].TrimEnd(',', ';', ':', ' ') + "…";
    }

    private static string? Pick(params string?[] candidates)
        => candidates.Select(Clean).FirstOrDefault(c => c is not null);

    // Espaços e quebras de linha juntos num só espaço; vazio → null.
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var joined = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? null : joined;
    }

    private static IEnumerable<string> Strings(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string? LinkImage(string html)
    {
        foreach (Match tag in LinkTag().Matches(html))
        {
            var attributes = Attributes(tag.Groups[1].Value);
            if (attributes.GetValueOrDefault("rel")?.Equals("image_src", StringComparison.OrdinalIgnoreCase) == true
                && attributes.TryGetValue("href", out var href)) return WebUtility.HtmlDecode(href);
        }
        return null;
    }

    private static Dictionary<string, string> Attributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attribute().Matches(tag))
        {
            var value = attribute.Groups[2].Success ? attribute.Groups[2].Value
                : attribute.Groups[3].Success ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
            result.TryAdd(attribute.Groups[1].Value, value);
        }
        return result;
    }

    private static string StripTags(string html) => Tag().Replace(html, " ");

    [GeneratedRegex(@"<meta\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"<link\b([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<p\b[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParagraphTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex("""([\w:.-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))""")]
    private static partial Regex Attribute();
}
