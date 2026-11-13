using System.Windows.Media;

namespace Eteris.Browser.Theme;

// «Cor do Éteris»: a cor de destaque da interface (faixa íris, aba ativa, pílulas, escudo, barra aurora, páginas
// internas) à escolha numa paleta, extraída da paisagem da nova aba ou copiada da cor de destaque do Windows.
// Só lógica: a aplicação aos recursos está em AccentManager.
public sealed record AccentChoice(string Key, string Name, Color Color);

public static class Accent
{
    public const string Default = "iris";
    public const string FromWallpaperKey = "wallpaper";
    public const string FromWindowsKey = "windows";

    // A assinatura original: violeta → ciano → verde-água → rosa.
    public static readonly Color[] IrisStops =
        [Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xF4, 0x72, 0xB6)];

    public static readonly IReadOnlyList<AccentChoice> Palette =
    [
        new("iris", "Íris", Color.FromRgb(0x8B, 0x5C, 0xF6)),
        new("ceu", "Céu", Color.FromRgb(0x3B, 0x82, 0xF6)),
        new("ciano", "Ciano", Color.FromRgb(0x22, 0xD3, 0xEE)),
        new("agua", "Verde-água", Color.FromRgb(0x34, 0xD3, 0x99)),
        new("lima", "Lima", Color.FromRgb(0xA3, 0xE6, 0x35)),
        new("ambar", "Âmbar", Color.FromRgb(0xF5, 0x9E, 0x0B)),
        new("coral", "Coral", Color.FromRgb(0xF9, 0x73, 0x16)),
        new("rosa", "Rosa", Color.FromRgb(0xF4, 0x72, 0xB6)),
        new("grafite", "Grafite", Color.FromRgb(0x9C, 0xA3, 0xAF)),
    ];

    // Contraste mínimo de um elemento de interface contra o fundo (WCAG 1.4.11): 3:1.
    public const double MinContrast = 3.0;
    private static readonly Color DarkFrame = Color.FromRgb(0x0D, 0x0F, 0x15);
    private static readonly Color LightFrame = Color.FromRgb(0xE9, 0xEC, 0xF3);

    public static bool IsValidChoice(string value)
        => value is FromWallpaperKey or FromWindowsKey || Palette.Any(c => c.Key == value);

    public static Color? PaletteColor(string key) => Palette.FirstOrDefault(c => c.Key == key)?.Color;

    // Cor base para a escolha guardada. Sem amostra da paisagem ou sem cor do Windows, volta ao íris.
    public static Color Resolve(string choice, Func<Color?> wallpaper, Func<Color?> windows)
        => choice switch
        {
            FromWallpaperKey => wallpaper() ?? PaletteColor(Default)!.Value,
            FromWindowsKey => windows() ?? PaletteColor(Default)!.Value,
            _ => PaletteColor(choice) ?? PaletteColor(Default)!.Value,
        };

    // Os quatro tons da faixa a partir de uma cor base: a base e três vizinhas no círculo cromático, com os mesmos
    // desvios da assinatura original (violeta → ciano → verde-água → rosa). Para a base original, a original.
    public static Color[] Gradient(Color baseColor, bool light = false)
    {
        var visible = EnsureVisible(baseColor, light);
        // A assinatura original tal e qual no tema escuro; no claro, cada tom escurecido só o preciso para se ver.
        if (visible == IrisStops[0]) return IrisStops.Select(stop => EnsureVisible(stop, light)).ToArray();
        var (h, s, l) = ToHsl(visible);
        // Cinzentos não têm matiz: a faixa fica em tons do mesmo cinzento (mais claros no escuro, mais escuros no claro).
        if (s < 0.1)
        {
            var direction = light ? -1 : 1;
            return [visible, Shade(visible, 0.12 * direction, light), Shade(visible, 0.2 * direction, light), Shade(visible, 0.06 * direction, light)];
        }
        return
        [
            visible,
            EnsureVisible(FromHsl(h - 70, s, l), light),
            EnsureVisible(FromHsl(h - 100, s, l), light),
            EnsureVisible(FromHsl(h + 72, s, l), light),
        ];
    }

    // Garante que a cor se vê contra a moldura do tema (≥ 3:1): clareia no escuro, escurece no claro.
    public static Color EnsureVisible(Color color, bool light)
    {
        var frame = light ? LightFrame : DarkFrame;
        var (h, s, l) = ToHsl(color);
        var step = light ? -0.03 : 0.03;
        var result = color;
        for (var i = 0; i < 30 && Contrast(result, frame) < MinContrast; i++)
        {
            l = Math.Clamp(l + step, 0, 1);
            result = FromHsl(h, s, l);
        }
        return result;
    }

    // O ponto «fora de casa» das abas fixadas (7 px, no canto do ícone): o 1.º degrau da faixa, clareado no escuro ou
    // escurecido no claro (o mesmo matiz) até ter 3:1 contra todos os fundos de uma aba do tema — a moldura, a aba
    // selecionada e a superfície da aba ativa (no escuro, até à cor de um site com SiteTint.MaxChromeLuminance; no claro,
    // #F6F7FB). Os degraus da faixa só garantem a moldura.
    public static Color HomeDot(Color stop, bool light)
    {
        Color[] backgrounds = light
            ? [LightFrame, Color.FromRgb(0xE1, 0xE5, 0xF2), Color.FromRgb(0xF6, 0xF7, 0xFB)]
            : [DarkFrame, Color.FromRgb(0x2A, 0x2F, 0x45), SiteTint.Base, BrightestDarkChrome];
        var (h, s, l) = ToHsl(stop);
        var step = light ? -0.02 : 0.02;
        var result = stop;
        for (var i = 0; i < 60 && backgrounds.Min(b => Contrast(result, b)) < MinContrast; i++)
        {
            l = Math.Clamp(l + step, 0, 1);
            result = FromHsl(h, s, l);
        }
        return result;
    }

    // O cinzento mais claro que a barra pode ter no tema escuro (a cor de um site, limitada a SiteTint.MaxChromeLuminance).
    private static readonly Color BrightestDarkChrome = Enumerable.Range(0, 256).Select(v => Color.FromRgb((byte)v, (byte)v, (byte)v))
        .Last(c => SiteTint.Luminance(c) <= SiteTint.MaxChromeLuminance);

    // Texto por cima de uma pílula com a cor de destaque: grafite ou branco, o que contrastar mais.
    public static Color TextOn(Color accent)
        => Contrast(accent, Color.FromRgb(0x0B, 0x0D, 0x13)) >= Contrast(accent, Colors.White)
            ? Color.FromRgb(0x0B, 0x0D, 0x13)
            : Colors.White;

    public static double Contrast(Color a, Color b)
    {
        var la = SiteTint.Luminance(a);
        var lb = SiteTint.Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // Cor dominante de uma imagem (pixéis já reduzidos): o matiz mais frequente entre os pixéis com cor, pesado
    // pela saturação, devolvido com saturação e luminosidade de destaque. Null se a imagem for cinzenta.
    public static Color? Dominant(IReadOnlyList<Color> pixels)
    {
        const int bins = 24;
        var weights = new double[bins];
        var saturationSum = new double[bins];
        var count = new int[bins];
        foreach (var pixel in pixels)
        {
            var (h, s, l) = ToHsl(pixel);
            if (s < 0.18 || l < 0.12 || l > 0.9) continue;
            var bin = (int)(h / 360 * bins) % bins;
            weights[bin] += s;
            saturationSum[bin] += s;
            count[bin]++;
        }
        var best = Array.IndexOf(weights, weights.Max());
        if (count[best] == 0) return null;
        var hue = (best + 0.5) * 360 / bins;
        var saturation = Math.Clamp(saturationSum[best] / count[best] + 0.15, 0.5, 0.85);
        return FromHsl(hue, saturation, 0.6);
    }

    // Cor de destaque do Windows: valor DWORD «AccentColor» em HKCU\Software\Microsoft\Windows\DWM, em ABGR.
    public static Color FromDwmAccent(int abgr)
        => Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        if (s <= 0) return Gray(l);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromRgb(Channel(p, q, h / 360 + 1.0 / 3), Channel(p, q, h / 360), Channel(p, q, h / 360 - 1.0 / 3));
    }

    private static Color Gray(double l) => Color.FromRgb((byte)Math.Round(l * 255), (byte)Math.Round(l * 255), (byte)Math.Round(l * 255));

    private static Color Shade(Color c, double amount, bool light)
    {
        var (h, s, l) = ToHsl(c);
        return EnsureVisible(FromHsl(h, s, Math.Clamp(l + amount, 0, 1)), light);
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        var v = t < 1.0 / 6 ? p + (q - p) * 6 * t
            : t < 0.5 ? q
            : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
            : p;
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }
}
