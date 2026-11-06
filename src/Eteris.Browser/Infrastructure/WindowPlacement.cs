using System.IO;
using System.Text.Json;
using System.Windows;

namespace Eteris.Browser.Infrastructure;

// Tamanho e posição da janela entre sessões (em unidades WPF), validados contra o ecrã atual.
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
{
    public const double MinWidth = 560;
    public const double MinHeight = 400;

    public static WindowPlacement? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path)) : null; }
        catch (Exception) { return null; }
    }

    public static void Save(string path, WindowPlacement placement)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(placement)); }
        catch (Exception) { }
    }

    // null quando a janela ficaria praticamente fora do ecrã (ex.: um monitor que foi desligado).
    public WindowPlacement? FitTo(Rect screen)
    {
        if (Width < MinWidth || Height < MinHeight) return null;
        var width = Math.Min(Width, screen.Width);
        var height = Math.Min(Height, screen.Height);
        var visible = Rect.Intersect(new Rect(Left, Top, width, height), screen);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 120) return null;
        var left = Math.Clamp(Left, screen.Left, screen.Right - width);
        var top = Math.Clamp(Top, screen.Top, screen.Bottom - height);
        return this with { Left = left, Top = top, Width = width, Height = height };
    }
}
