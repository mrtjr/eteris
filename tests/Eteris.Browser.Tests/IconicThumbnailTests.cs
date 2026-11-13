using System.Reflection;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// A miniatura neutra de uma janela privada trancada (Infrastructure/IconicThumbnail.cs): na barra de tarefas, no Alt+Tab e
// na vista de tarefas o DWM mostra uma imagem nossa, com o fundo da janela privada e um cadeado, sem nada da janela. O
// caso que a exige é a janela minimizada quando tranca: o DWM guardava o instantâneo de antes de minimizar.
public class IconicThumbnailTests
{
    // Os valores de um/dwmapi.h e um/WinUser.h (SDK 10.0.28000).
    [Fact]
    public void Constants_AreTheDwmOnes()
    {
        Assert.Equal(7, IconicThumbnail.DWMWA_FORCE_ICONIC_REPRESENTATION);
        Assert.Equal(10, IconicThumbnail.DWMWA_HAS_ICONIC_BITMAP);
        Assert.Equal(11, IconicThumbnail.DWMWA_DISALLOW_PEEK);
        Assert.Equal(13, IconicThumbnail.DWMWA_CLOAK);
        Assert.Equal(0x0323, IconicThumbnail.WM_DWMSENDICONICTHUMBNAIL);
        Assert.Equal(0x0326, IconicThumbnail.WM_DWMSENDICONICLIVEPREVIEWBITMAP);
    }

    [Fact]
    public void MaxSize_ReadsWidthFromTheHighWord()
    {
        Assert.Equal((200, 120), IconicThumbnail.MaxSize((200 << 16) | 120));
        Assert.Equal((0xFFFF, 1), IconicThumbnail.MaxSize(unchecked((nint)(((long)0xFFFF << 16) | 1))));
    }

    [Fact]
    public void Fit_KeepsTheWindowShapeInsideTheMaximum()
    {
        var (w, h) = IconicThumbnail.Fit(200, 120, 1600, 900);
        Assert.InRange(w, 1, 200);
        Assert.InRange(h, 1, 120);
        Assert.InRange(w * 9.0 / 16, h - 1, h + 1);
        Assert.Equal((1, 1), IconicThumbnail.Fit(1, 1, 0, 0));
        Assert.Equal((1, 1), IconicThumbnail.Fit(0, 0, 1600, 900));
        // Uma janela alta: a altura manda.
        var (tw, th) = IconicThumbnail.Fit(200, 120, 600, 1200);
        Assert.Equal(120, th);
        Assert.InRange(tw, 59, 61);
    }

    [Fact]
    public void Pixels_AreOpaqueAndNeutral()
    {
        var pixels = IconicThumbnail.Pixels(64, 40);
        Assert.Equal(64 * 40, pixels.Length);
        Assert.All(pixels, p => Assert.Equal(0xFFu, p >> 24));
        Assert.Equal(0xFF140B24u, pixels[0]);
        Assert.Equal(0xFF140B24u, pixels[^1]);
        Assert.Equal(0xFFB9A7FFu, pixels[20 * 64 + 32]);
        // Só as duas cores (sem texto, sem nada da janela).
        Assert.Equal([0xFF140B24u, 0xFFB9A7FFu], pixels.Distinct().Order().ToArray());
        Assert.True(Contrast(0x140B24, 0xB9A7FF) >= 3, $"contraste {Contrast(0x140B24, 0xB9A7FF):0.00}:1");
        Assert.Single(IconicThumbnail.Pixels(1, 1));
        Assert.Empty(IconicThumbnail.Pixels(0, 5));
    }

    [Fact]
    public void Pixels_TakeNoWindowData()
    {
        foreach (var name in new[] { nameof(IconicThumbnail.Pixels), nameof(IconicThumbnail.Fit), nameof(IconicThumbnail.MaxSize) })
        {
            var method = typeof(IconicThumbnail).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            Assert.All(method.GetParameters(), p => Assert.Contains(p.ParameterType, new[] { typeof(int), typeof(nint) }));
        }
    }

    // A fórmula da WCAG 2 (luminância relativa).
    private static double Contrast(int a, int b)
    {
        static double Channel(int c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(int rgb) => 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
