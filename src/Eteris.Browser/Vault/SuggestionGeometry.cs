using System.Windows;

namespace Eteris.Browser.Vault;

// Onde fica a lista de sugestões, em DIP da camada por cima da página. Width é a largura da lista; Above diz que fica
// por cima do campo (sem lugar por baixo).
public sealed record SuggestionPlacement(double X, double Y, double Width, bool Above);

// A conta (pura) da posição da lista (docs/palavras-passe.md, C3). O retângulo do campo vem do script em píxeis CSS da
// área visível da página de topo; em DIP do WPF vale CSS × zoom da página — a escala do ecrã não entra (o Chromium
// desenha CSS × zoom × escala em píxeis físicos e o WPF divide pela mesma escala). page é a área da página da aba na
// camada (no ecrã dividido, a metade dela): a lista nunca sai dela.
public static class SuggestionGeometry
{
    public const double Gap = 2;
    public const double MinWidth = 260;

    public static SuggestionPlacement? Place(Rect page, VaultRect field, double zoom, double listHeight)
    {
        if (page.IsEmpty || page.Width <= 0 || page.Height <= 0 || zoom <= 0 || !double.IsFinite(zoom)) return null;
        var left = page.Left + field.X * zoom;
        var top = page.Top + field.Y * zoom;
        var width = field.Width * zoom;
        var height = field.Height * zoom;
        // O campo tem de estar (pelo menos em parte) na página à vista.
        if (left + width <= page.Left || left >= page.Right || top + height <= page.Top || top >= page.Bottom) return null;
        var listWidth = Math.Min(Math.Max(width, MinWidth), page.Width);
        var x = Math.Max(page.Left, Math.Min(left, page.Right - listWidth));
        var below = top + height + Gap;
        var above = top - Gap - listHeight;
        return below + listHeight > page.Bottom && above >= page.Top
            ? new SuggestionPlacement(x, above, listWidth, true)
            : new SuggestionPlacement(x, below, listWidth, false);
    }
}
