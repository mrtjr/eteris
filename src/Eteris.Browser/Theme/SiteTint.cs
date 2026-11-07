using System.Globalization;
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

    public static Color? Parse(string css)
    {
        var text = css.Trim();
        if (text.Length == 0) return null;

        var rgb = RgbPattern().Match(text);
        if (rgb.Success)
        {
            var alpha = rgb.Groups[4].Success ? double.Parse(rgb.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            if (alpha < 0.5) return null;
            return Color.FromRgb(Channel(rgb.Groups[1].Value), Channel(rgb.Groups[2].Value), Channel(rgb.Groups[3].Value));
        }

        if (text.StartsWith('#') && text.Length == 4)
            text = $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}";
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color color && color.A >= 128)
                return Color.FromRgb(color.R, color.G, color.B);
        }
        catch (FormatException) { }
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

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*([\d.]+)\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbPattern();
}
