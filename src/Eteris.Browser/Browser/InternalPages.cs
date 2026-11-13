using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Browser;

// Ações das páginas eteris:// (apagar histórico, mudar definições) levam um token aleatório desta sessão.
// O esquema é "display isolated" (um site não o pede diretamente), o token impede pedidos forjados de outras origens e,
// desde a auditoria 3, a fábrica só responde a quem é página interna (Serves; ver docs/sandbox.md, «Páginas internas»).
public static class InternalPages
{
    public static readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsAuthorized(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Query(uri, "t") is { } token
           && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(Token));

    // As páginas internas seguem o tema da aplicação (o servidor marca <html class="light">) e a Cor do Éteris
    // (um <style> no <head> redefine a variável --iris, que todas as páginas usam, --accent com a cor base e --accent-2
    // com o segundo tom: os controlos ligados — pílulas, interruptores — pintam-se com os dois).
    public static string Themed(string html, bool light) => Themed(html, light, AccentManager.Stops);

    public static string Themed(string html, bool light, IReadOnlyList<Color> accentStops)
    {
        if (light)
            html = html.Replace("<html lang=\"pt\">", "<html lang=\"pt\" class=\"light\">")
                       .Replace("<meta name=\"color-scheme\" content=\"dark\">", "<meta name=\"color-scheme\" content=\"light\">");
        if (!accentStops.SequenceEqual(Accent.IrisStops))
        {
            var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (head >= 0) html = html.Insert(head, AccentStyle(accentStops));
        }
        return html;
    }

    public static string AccentStyle(IReadOnlyList<Color> stops)
        => "<style id=\"eteris-accent\">:root { --iris: " + IrisCss(stops) + "; --accent: " + Accent.Hex(stops[0])
           + "; --accent-2: " + Accent.Hex(stops[1]) + "; }</style>";

    // Para as páginas internas já abertas quando a cor muda (corrido pelo DevTools, ver PageScript): só as variáveis.
    public static string AccentScript(IReadOnlyList<Color> stops)
        => stops.SequenceEqual(Accent.IrisStops)
            ? "document.documentElement.style.removeProperty('--iris'); document.documentElement.style.removeProperty('--accent'); document.documentElement.style.removeProperty('--accent-2');"
            : $"document.documentElement.style.setProperty('--iris', '{IrisCss(stops)}'); document.documentElement.style.setProperty('--accent', '{Accent.Hex(stops[0])}'); document.documentElement.style.setProperty('--accent-2', '{Accent.Hex(stops[1])}');";

    private static string IrisCss(IReadOnlyList<Color> stops) => "linear-gradient(90deg, " + string.Join(", ", stops.Select(Accent.Hex)) + ")";

    // ---- Quem pede (INT-07, VAU-09) ----
    // O token trava pedidos forjados, mas uma página web conseguia embutir as páginas internas (um 302 para eteris://
    // numa moldura) e ler o que a raiz das palavras-passe trazia. A porta fica agora também no processo do navegador:
    // a fábrica do esquema pergunta quem é o documento de topo do separador. O tipo de recurso é declarado pelo processo
    // da página: a porta trava molduras e sub-recursos, não um processo comprometido que diga «MainFrame» — por isso a
    // raiz do cofre não leva registos (vêm por /list).

    // Uma página do Éteris (eteris://…), lida como o Route a lê (Uri, não prefixos de texto).
    public static bool IsInternal(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == EterisSchemeHandlerFactory.SchemeName && uri.Host.Length > 0;

    // Os anfitriões do cofre: as rotas deles (a lista, mostrar, copiar…) só respondem à própria página.
    private static bool IsVaultHost(string host) => host is "passwords" or "autofill";

    // Responde-se a este pedido? Abrir uma página (o frame principal, caminho «/») vale sempre; no cofre, o resto só
    // para a própria página; outro pedido do frame principal vale (as ações continuam a exigir o token); molduras e
    // sub-recursos só quando o documento de topo é uma página interna.
    public static bool Serves(string? topUrl, string requestUrl, bool mainFrameRequest)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) || request.Scheme != EterisSchemeHandlerFactory.SchemeName) return false;
        if (mainFrameRequest && request.AbsolutePath == "/") return true;
        if (IsVaultHost(request.Host))
            return Uri.TryCreate(topUrl, UriKind.Absolute, out var top) && top.Scheme == EterisSchemeHandlerFactory.SchemeName
                   && string.Equals(top.Host, request.Host, StringComparison.OrdinalIgnoreCase);
        if (mainFrameRequest) return true;
        return IsInternal(topUrl);
    }

    // OnBeforeBrowse: um redirecionamento para eteris:// (o 302 de um site) e uma moldura eteris:// numa página que não
    // é interna são cancelados. Abrir uma página interna pela barra, um favorito ou um link interno continua.
    public static bool BlocksNavigation(string? topUrl, string targetUrl, bool isMainFrame, bool isRedirect)
        => IsInternal(targetUrl) && (isRedirect || (!isMainFrame && !IsInternal(topUrl)));

    // Logo depois de uma navegação (um F5, sobretudo) o frame principal ainda não tem endereço na thread de rede (""):
    // vale então a navegação em curso, anotada pelo PrivacyRequestHandler quando o pedido da página sai.
    private const int MaxNotes = 4096;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> Navigations = new();

    public static void NoteNavigation(int browserId, string url)
    {
        if (Navigations.Count > MaxNotes) Navigations.Clear();
        Navigations[browserId] = url;
    }

    public static string? TopOf(int browserId, string? mainUrl)
        => !string.IsNullOrEmpty(mainUrl) ? mainUrl : Navigations.TryGetValue(browserId, out var url) ? url : mainUrl;

    public static bool LightTheme => Theme.ThemeManager.Current == Theme.AppTheme.Light;

    // Todos os parâmetros da consulta (o primeiro de cada nome).
    public static Dictionary<string, string> QueryAll(Uri uri)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            all.TryAdd(parts[0], parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }
        return all;
    }

    public static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == name) return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }
}
