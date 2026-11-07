using System.Windows.Media;

namespace Eteris.Browser.Theme;

// Cor OPACA por baixo da janela da página (HwndHost). Quando o Chromium apresenta um fotograma ainda vazio (aba nova,
// troca de documento, janela a crescer), é esta cor que se vê, e não o Mica (escuro esverdeado) nem lixo da janela.
// Segue o que a página vai mostrar: branco nas páginas web (como o Chrome), o fundo das páginas internas do Éteris no
// tema em uso, e quase preto num site escurecido pelo Éteris (DarkPages: invert(.9) de branco = #1A1A1A).
public static class PageBackdrop
{
    public static readonly Color Web = Colors.White;
    public static readonly Color DarkenedSite = Color.FromRgb(0x1A, 0x1A, 0x1A);
    public static readonly Color InternalDark = Color.FromRgb(0x0B, 0x0D, 0x13);
    public static readonly Color InternalLight = Color.FromRgb(0xF3, 0xF4, 0xF9);

    public static bool IsWeb(string? address)
        => address is not null && (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                   || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static Color For(string? address, bool lightTheme, bool darkenedSite)
        => IsWeb(address) ? (darkenedSite ? DarkenedSite : Web) : (lightTheme ? InternalLight : InternalDark);
}
