using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Browser;

// O que o cartão «Espreitar» mostra de um link: título, site, descrição, imagem (og:image), tempo de leitura e os
// primeiros parágrafos do artigo.
public sealed record LinkPreviewInfo(string Url, string Title, string Site, string? Description, string? ImageUrl,
    IReadOnlyList<string> Paragraphs, int Words, string? Byline = null)
{
    public int ReadingMinutes => LinkPreview.ReadingMinutes(Words);

    // «3 min de leitura»; vazio quando não há texto que chegue para contar.
    public string ReadingTime => ReadingMinutes == 0 ? string.Empty : $"{ReadingMinutes} min de leitura";
}

// O que uma tecla faz no cartão «Espreitar».
public enum GlanceAction { None, Close, Open, OpenSideBySide, ReadLater }

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

    // Resultado do script na aba escondida: cabeçalho (meta) + artigo (Readability) + texto de recurso.
    public static string Script => ReaderScripts.WithReadability(ScriptTemplate);

    private const string ScriptTemplate = """
        (() => {
          const cap = (text, max) => (text || '').length > max ? (text || '').slice(0, max) : (text || '');
          let article = null;
          try {
            if (typeof Readability === 'function') {
              const parsed = new Readability(document.cloneNode(true), { serializer: () => '' }).parse();
              if (parsed && (parsed.textContent || '').trim().length > 0) article = parsed;
            }
          } catch (e) { article = null; }
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
          });
        })()
        """;

    // Teclado no cartão: Esc fecha, Enter abre, Shift+Enter abre ao lado, Ctrl+Enter guarda para ler mais tarde
    // (na janela privada não há «Ler mais tarde»). Os botões não recebem foco, por isso estas são as únicas teclas.
    public static GlanceAction ActionFor(string key, bool shift, bool ctrl, bool isPrivate) => key switch
    {
        "Escape" => GlanceAction.Close,
        "Return" or "Enter" when ctrl && !shift => isPrivate ? GlanceAction.None : GlanceAction.ReadLater,
        "Return" or "Enter" when shift && !ctrl => GlanceAction.OpenSideBySide,
        "Return" or "Enter" when !shift && !ctrl => GlanceAction.Open,
        _ => GlanceAction.None,
    };

    public const string ReportPrefix = "\u0001eteris-glance ";

    // Alt+clique num link (botão esquerdo, só Alt): a página não navega nem transfere (é o que o Chromium faria com
    // Alt) e o Éteris recebe o endereço para o espreitar. Corre antes dos scripts da página, em captura na janela,
    // por isso a página não o consegue calar. Só links http(s) com o clique de verdade (isTrusted).
    public const string AltClickScript = """
        (() => {
          const debug = console.debug.bind(console);
          addEventListener('click', e => {
            if (!e.isTrusted || !e.altKey || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
            for (const node of (e.composedPath ? e.composedPath() : [e.target])) {
              if (!(node instanceof Element)) continue;
              if ((node.tagName === 'A' || node.tagName === 'AREA') && node.href && /^https?:/i.test(node.href)) {
                e.preventDefault();
                e.stopImmediatePropagation();
                debug('\u0001eteris-glance ' + node.href);
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
            var articleTitle = Clean(Text(root, "title"));
            var title = Pick(meta.Title, articleTitle) ?? HostOf(url);
            var site = Pick(meta.SiteName, Clean(Text(root, "site"))) ?? HostOf(url);
            var paragraphs = Paragraphs(text);
            if (paragraphs.Count == 0) paragraphs = meta.Paragraphs;
            var words = root.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.Number ? (int)w.GetDouble() : 0;
            if (words == 0) words = CountWords(text);
            var description = meta.Description;
            // Uma descrição igual ao início do artigo repete-se: fica só o artigo.
            if (description is not null && paragraphs.Count > 0 && paragraphs[0].StartsWith(description.TrimEnd('…', '.', ' '), StringComparison.Ordinal))
                description = null;
            return new LinkPreviewInfo(url, title, site, description, meta.ImageUrl, paragraphs, words, Clean(Text(root, "byline")));
        }
        catch (JsonException) { return null; }
    }

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
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var budget = maxChars;
        foreach (var raw in text.Split('\n'))
        {
            var line = Clean(raw);
            if (line is null || line.Length < MinParagraphChars) continue;
            if (line.Length > budget)
            {
                if (budget >= MinParagraphChars) result.Add(Truncate(line, budget));
                break;
            }
            result.Add(line);
            budget -= line.Length;
            if (result.Count >= max) break;
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
