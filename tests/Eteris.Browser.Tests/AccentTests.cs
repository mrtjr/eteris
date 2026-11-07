using System.Windows.Media;
using Eteris.Browser.Data;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class AccentTests
{
    private static readonly Color Dark = Color.FromRgb(0x0D, 0x0F, 0x15);
    private static readonly Color Light = Color.FromRgb(0xE9, 0xEC, 0xF3);

    [Fact]
    public void Palette_Has8To10DistinctColors_WithIrisFirst()
    {
        Assert.InRange(Accent.Palette.Count, 8, 10);
        Assert.Equal(Accent.Default, Accent.Palette[0].Key);
        Assert.Equal(Accent.Palette.Count, Accent.Palette.Select(c => c.Key).Distinct().Count());
        Assert.Equal(Accent.Palette.Count, Accent.Palette.Select(c => c.Color).Distinct().Count());
    }

    [Theory]
    [InlineData("iris", true)]
    [InlineData("coral", true)]
    [InlineData("wallpaper", true)]
    [InlineData("windows", true)]
    [InlineData("neon", false)]
    [InlineData("", false)]
    public void IsValidChoice_AcceptsPaletteKeysAndSources(string value, bool valid)
        => Assert.Equal(valid, Accent.IsValidChoice(value));

    [Fact]
    public void Setting_DefaultsToIris_AndRejectsUnknownColors()
    {
        Assert.Equal("iris", new UserSettings().Accent);
        Assert.Equal("coral", new UserSettings().With("accent", "coral")!.Accent);
        Assert.Equal("windows", new UserSettings().With("accent", "windows")!.Accent);
        Assert.Null(new UserSettings().With("accent", "neon"));
    }

    [Fact]
    public void Gradient_ForIris_IsTheOriginalSignature()
        => Assert.Equal(Accent.IrisStops, Accent.Gradient(Accent.PaletteColor("iris")!.Value));

    // Cada tom da faixa tem de se ver contra a moldura do tema (3:1, o mínimo para elementos de interface).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gradient_EveryStopIsVisibleAgainstTheFrame(bool light)
    {
        var frame = light ? Light : Dark;
        foreach (var choice in Accent.Palette)
        {
            var stops = Accent.Gradient(choice.Color, light);
            Assert.Equal(4, stops.Length);
            Assert.All(stops, stop => Assert.True(Accent.Contrast(stop, frame) >= Accent.MinContrast, $"{choice.Key} {Accent.Hex(stop)} ({(light ? "claro" : "escuro")})"));
        }
    }

    // A primeira cor da faixa é a escolhida (ou a sua versão visível): o utilizador reconhece a cor que escolheu.
    [Fact]
    public void Gradient_StartsWithTheChosenHue()
    {
        var coral = Accent.PaletteColor("coral")!.Value;
        var (expectedHue, _, _) = Accent.ToHsl(coral);
        var (hue, _, _) = Accent.ToHsl(Accent.Gradient(coral)[0]);
        Assert.InRange(Math.Abs(hue - expectedHue), 0, 3);
    }

    [Fact]
    public void EnsureVisible_LiftsDarkColorsOnDark_AndDeepensOnLight()
    {
        var navy = Color.FromRgb(0x10, 0x20, 0x60);
        Assert.True(Accent.Contrast(Accent.EnsureVisible(navy, light: false), Dark) >= Accent.MinContrast);
        var cream = Color.FromRgb(0xF0, 0xE6, 0xB0);
        Assert.True(Accent.Contrast(Accent.EnsureVisible(cream, light: true), Light) >= Accent.MinContrast);
        // Uma cor já visível não muda.
        var cyan = Accent.PaletteColor("ciano")!.Value;
        Assert.Equal(cyan, Accent.EnsureVisible(cyan, light: false));
    }

    [Fact]
    public void TextOn_PicksTheMoreContrastingInk()
    {
        Assert.Equal(Colors.White, Accent.TextOn(Color.FromRgb(0x1E, 0x3A, 0x8A)));
        Assert.Equal(Color.FromRgb(0x0B, 0x0D, 0x13), Accent.TextOn(Accent.PaletteColor("lima")!.Value));
    }

    [Fact]
    public void Resolve_FallsBackToIris_WhenASourceHasNothing()
    {
        var iris = Accent.PaletteColor("iris")!.Value;
        Assert.Equal(iris, Accent.Resolve("wallpaper", () => null, () => null));
        Assert.Equal(iris, Accent.Resolve("windows", () => null, () => null));
        Assert.Equal(iris, Accent.Resolve("nonsense", () => null, () => null));
        Assert.Equal(Colors.Red, Accent.Resolve("wallpaper", () => Colors.Red, () => null));
        Assert.Equal(Colors.Blue, Accent.Resolve("windows", () => null, () => Colors.Blue));
        Assert.Equal(Accent.PaletteColor("coral"), Accent.Resolve("coral", () => Colors.Red, () => Colors.Blue));
    }

    // Uma paisagem de mar e céu: o azul domina mesmo com alguma areia e rocha cinzenta.
    [Fact]
    public void Dominant_FindsTheMostFrequentSaturatedHue()
    {
        var pixels = new List<Color>();
        for (var i = 0; i < 60; i++) pixels.Add(Color.FromRgb(0x2A, 0x6F, 0xB5));   // mar
        for (var i = 0; i < 25; i++) pixels.Add(Color.FromRgb(0xC9, 0xA8, 0x6A));   // areia
        for (var i = 0; i < 40; i++) pixels.Add(Color.FromRgb(0x50, 0x52, 0x55));   // rocha (cinzenta: não conta)
        var dominant = Accent.Dominant(pixels);
        Assert.NotNull(dominant);
        var (hue, s, l) = Accent.ToHsl(dominant!.Value);
        Assert.InRange(hue, 195, 225);
        Assert.InRange(s, 0.5, 0.85);
        Assert.InRange(l, 0.55, 0.65);
    }

    [Fact]
    public void Dominant_GrayImage_HasNoColor()
        => Assert.Null(Accent.Dominant([Color.FromRgb(0x40, 0x40, 0x40), Color.FromRgb(0x80, 0x80, 0x82), Colors.White]));

    [Fact]
    public void FromDwmAccent_ReadsAbgr()
        => Assert.Equal(Color.FromRgb(0x00, 0x78, 0xD4), Accent.FromDwmAccent(unchecked((int)0xFFD47800)));

    [Theory]
    [InlineData(0x8B, 0x5C, 0xF6)]
    [InlineData(0x22, 0xD3, 0xEE)]
    [InlineData(0x10, 0x10, 0x10)]
    public void Hsl_RoundTrips(byte r, byte g, byte b)
    {
        var (h, s, l) = Accent.ToHsl(Color.FromRgb(r, g, b));
        var back = Accent.FromHsl(h, s, l);
        Assert.InRange(Math.Abs(back.R - r) + Math.Abs(back.G - g) + Math.Abs(back.B - b), 0, 3);
    }
}
