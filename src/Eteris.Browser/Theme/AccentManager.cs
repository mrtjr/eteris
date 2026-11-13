using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Eteris.Browser.Theme;

// Aplica a «Cor do Éteris» (ver Accent) à faixa íris partilhada por toda a interface. O WPF congela os pincéis
// dos recursos da aplicação e os que servem de valor a Setters de estilos, por isso o IrisBrush não pode ser
// retintado no sítio: cada mudança põe nos recursos da aplicação um pincel novo (com os degraus animados desde as
// cores antigas, o que também o impede de congelar), e toda a interface lê-o por DynamicResource. Junto vão a
// tinta do texto sobre a cor (AccentInkBrush, grafite ou branco) e a cor lisa da seleção de texto.
public static class AccentManager
{
    private static readonly TimeSpan Transition = TimeSpan.FromMilliseconds(420);

    // Cor base e degraus em vigor (as páginas internas e a barra aurora lêem daqui).
    public static Color Current { get; private set; } = Accent.IrisStops[0];
    public static Color[] Stops { get; private set; } = (Color[])Accent.IrisStops.Clone();

    // O ponto «fora de casa» das abas fixadas (Accent.HomeDot), lido pelo HomeDotBrush.
    public static Color HomeDot { get; private set; } = Accent.HomeDot(Accent.IrisStops[0], light: false);

    public static event Action? Changed;

    // Amostra da paisagem da nova aba (ligada pela janela, que tem as imagens); null enquanto não houver.
    public static Func<Color?>? WallpaperSampler { get; set; }

    public static void Apply(string choice, bool light)
    {
        var baseColor = Accent.Resolve(choice, () => WallpaperSampler?.Invoke(), ReadWindowsAccent);
        var stops = Accent.Gradient(baseColor, light);
        // O ponto das abas fixadas depende também do tema, mesmo quando os degraus não mudam com ele.
        var dot = Accent.HomeDot(stops[0], light);
        if (stops.SequenceEqual(Stops) && baseColor == Current && dot == HomeDot) return;
        Current = baseColor;
        Stops = stops;
        HomeDot = dot;
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            resources["IrisBrush"] = Recolored(resources["IrisBrush"] as LinearGradientBrush, stops);
            resources["AccentInkBrush"] = Frozen(new SolidColorBrush(Accent.TextOn(baseColor)));
            resources["AccentSelectionBrush"] = Frozen(new SolidColorBrush(baseColor));
            resources["HomeDotBrush"] = Frozen(new SolidColorBrush(dot));
        }
        Changed?.Invoke();
    }

    // Um pincel íris novo com a geometria e as cores em vigor do anterior, animado até aos degraus novos. Os
    // degraus animados tornam-no impossível de congelar, logo continua vivo mesmo dentro de estilos selados.
    public static LinearGradientBrush Recolored(LinearGradientBrush? previous, IReadOnlyList<Color> stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = previous?.StartPoint ?? new Point(0, 0),
            EndPoint = previous?.EndPoint ?? new Point(1, 0),
        };
        for (var i = 0; i < stops.Count; i++)
        {
            var source = previous is not null && i < previous.GradientStops.Count ? previous.GradientStops[i] : null;
            var offset = source?.Offset ?? (stops.Count == IrisOffsets.Length ? IrisOffsets[i] : stops.Count == 1 ? 0 : (double)i / (stops.Count - 1));
            brush.GradientStops.Add(new GradientStop(source?.Color ?? stops[i], offset));
        }
        Retint(brush, stops);
        return brush;
    }

    private static readonly double[] IrisOffsets = [0, 0.35, 0.65, 1];

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    // Degraus de um pincel íris (o da aplicação ou a barra aurora de uma janela) animados até às cores em vigor.
    public static void Retint(LinearGradientBrush? brush, IReadOnlyList<Color> stops)
    {
        if (brush is null || brush.IsFrozen) return;
        // Pincéis com mais degraus (a barra aurora repete o primeiro tom nas pontas, transparente) seguem pelo índice.
        for (var i = 0; i < brush.GradientStops.Count; i++)
        {
            var stop = brush.GradientStops[i];
            var target = stops[Math.Min(i, stops.Count - 1)];
            if (brush.GradientStops.Count > stops.Count)
                target = i == 0 || i == brush.GradientStops.Count - 1
                    ? Color.FromArgb(0, stops[0].R, stops[0].G, stops[0].B)
                    : stops[Math.Clamp(i - 1, 0, stops.Count - 1)];
            stop.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(target, Transition) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }
    }

    private static bool _watching;

    // Com «Do Windows», mudar a cor de destaque no Windows muda o Éteris na hora.
    public static void FollowWindowsChanges(Func<string> currentChoice, Func<bool> light)
    {
        if (_watching) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || currentChoice() != Accent.FromWindowsKey) return;
            Application.Current?.Dispatcher.InvokeAsync(() => Apply(Accent.FromWindowsKey, light()));
        };
    }

    // Cor de destaque do Windows (Definições › Personalização › Cores), lida do registo: sem WinRT.
    public static Color? ReadWindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            return key?.GetValue("AccentColor") is int value ? Accent.FromDwmAccent(value) : null;
        }
        catch (Exception) { return null; }
    }
}
