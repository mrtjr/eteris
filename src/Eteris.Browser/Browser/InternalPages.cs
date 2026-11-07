using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Browser;

// Ações das páginas eteris:// (apagar histórico, mudar definições) levam um token aleatório desta sessão.
// O esquema é "display isolated": nenhum site o consegue abrir, e o token impede pedidos forjados de outras origens.
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

    public static bool LightTheme => Theme.ThemeManager.Current == Theme.AppTheme.Light;

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
