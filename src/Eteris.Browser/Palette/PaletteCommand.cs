namespace Eteris.Browser.Palette;

public sealed record PaletteCommand(string Glyph, string Title, string Subtitle, string? Shortcut, Action Run);
