using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Eteris.Browser.Theme;

public static partial class SiteTint
{
    public static readonly Color Base = Color.FromRgb(0x1A, 0x1D, 0x27);

    // Texto #E9EBF2 sobre a barra precisa de contraste ≥ 7:1 → luminância de fundo ≤ ~0.07.
    public const double MaxChromeLuminance = 0.07;
    private const double LightSiteLuminance = 0.6;
    private const double BlendAmount = 0.45;

    // Pitada da cor do Espaço sobre a barra: notória lado a lado, discreta à vista (no tema claro ainda menos).
    public const double WorkspaceBlend = 0.16;
    public const double LightWorkspaceBlend = 0.07;

    // A cor do tema que a página declara (theme-color). O texto vem da página: só três formas fechadas, convertidas
    // aqui, e null para tudo o resto, sem nunca lançar. Nunca passa pelo conversor de cores do WPF, que também aceita
    // «ContextColor <endereço> …» e abre esse endereço no processo principal (MOT-02).
    public static Color? Parse(string css)
    {
        var text = css.Trim();
        if (text.Length == 0) return null;

        var rgb = RgbPattern().Match(text);
        if (rgb.Success)
        {
            var alpha = 1.0;
            if (rgb.Groups[4].Success
                && !double.TryParse(rgb.Groups[4].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out alpha))
                return null;
            if (alpha < 0.5) return null;
            return Color.FromRgb(Channel(rgb.Groups[1].Value), Channel(rgb.Groups[2].Value), Channel(rgb.Groups[3].Value));
        }

        if (HexPattern().IsMatch(text))
        {
            var hex = text[1..];
            if (hex.Length == 3) hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            // #rrggbbaa: o alfa é o último par, como no CSS.
            if (hex.Length == 8 && Convert.ToByte(hex[6..8], 16) < 128) return null;
            return Color.FromRgb(Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }

        // Nomes: a tabela fechada de System.Windows.Media.Colors («transparent» tem alfa 0 e dá null).
        if (NamePattern().IsMatch(text)
            && typeof(Colors).GetProperty(text, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null) is Color named
            && named.A >= 128)
            return Color.FromRgb(named.R, named.G, named.B);
        return null;
    }

    // Tom da barra para a cor de um site, ou null para manter o tema base (sites claros).
    // Cores escuras são adotadas tal e qual, para a barra continuar o site sem costura.
    public static Color? ForChrome(Color site)
    {
        var luminance = Luminance(site);
        if (luminance > LightSiteLuminance) return null;
        if (luminance <= MaxChromeLuminance) return site;
        var tint = Blend(Base, site, BlendAmount);
        while (Luminance(tint) > MaxChromeLuminance)
            tint = Color.FromRgb((byte)(tint.R * 0.9), (byte)(tint.G * 0.9), (byte)(tint.B * 0.9));
        return tint;
    }

    // Tom da barra num Espaço com cor: a cor do Espaço entra um pouco no tom que a barra já tinha (do tema ou do
    // site), sem tirar contraste ao texto — no escuro a luminância continua abaixo do limite, no claro a barra
    // continua clara.
    public static Color WithWorkspace(Color chrome, Color space, bool light)
    {
        var tint = Blend(chrome, space, light ? LightWorkspaceBlend : WorkspaceBlend);
        if (light) return tint;
        while (Luminance(tint) > MaxChromeLuminance)
            tint = Color.FromRgb((byte)(tint.R * 0.9), (byte)(tint.G * 0.9), (byte)(tint.B * 0.9));
        return tint;
    }

    public static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static Color Blend(Color a, Color b, double amount)
        => Color.FromRgb(Mix(a.R, b.R, amount), Mix(a.G, b.G, amount), Mix(a.B, b.B, amount));

    private static byte Mix(byte a, byte b, double amount) => (byte)Math.Round(a + (b - a) * amount);

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static byte Channel(string value) => (byte)Math.Clamp(int.Parse(value, CultureInfo.InvariantCulture), 0, 255);

    // [0-9] e não \d: \d aceita algarismos de outras escritas (árabes, devanágari), que o int.Parse depois recusa.
    [GeneratedRegex(@"^rgba?\(\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})\s*(?:,\s*([0-9]*\.?[0-9]+)\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbPattern();

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"^[a-zA-Z]{3,24}$")]
    private static partial Regex NamePattern();
}
