using System.Windows;

namespace Eteris.Browser.Watching;

// O recorte ao vivo (ClipWindow): o tamanho da página dentro da janela pequena, a partir do pedaço escolhido, e onde cada
// recorte aparece. Contas puras, em unidades do WPF (os px CSS da página a 100 % são as mesmas unidades).
public static class ClipLayout
{
    public const int MaxOpen = 3;
    public const double MinWidth = 200, MinHeight = 120;
    private const double Margin = 16, Step = 24;

    public static bool CanOpen(int open) => open < MaxOpen;

    // Mínimo 200×120; no máximo metade da largura e da altura da área de trabalho, a manter a proporção quando encolhe.
    // O retângulo vem da página: um valor sem sentido (NaN, negativo, infinito) dá o mínimo.
    public static Size Size(double w, double h, Rect workArea)
    {
        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return new Size(MinWidth, MinHeight);
        var maxW = Math.Max(MinWidth, workArea.Width / 2);
        var maxH = Math.Max(MinHeight, workArea.Height / 2);
        double width = w, height = h;
        if (w > maxW || h > maxH)
        {
            // O lado que mais passa fica no máximo; o outro na mesma proporção (multiplicar antes de dividir: as contas
            // redondas ficam exatas).
            if (w / maxW >= h / maxH)
            {
                width = maxW;
                height = h * maxW / w;
            }
            else
            {
                height = maxH;
                width = w * maxH / h;
            }
        }
        return new Size(Math.Min(maxW, Math.Max(MinWidth, Math.Round(width))), Math.Min(maxH, Math.Max(MinHeight, Math.Round(height))));
    }

    // Canto inferior direito, margem de 16; cada recorte seguinte 24 acima e à esquerda do anterior. Nunca fora da área.
    public static Point Place(Size size, Rect workArea, int index)
    {
        var offset = Step * Math.Max(0, index);
        return new Point(Math.Max(workArea.Left, workArea.Right - Margin - size.Width - offset),
            Math.Max(workArea.Top, workArea.Bottom - Margin - size.Height - offset));
    }

    // O lugar de um recorte novo: o 1.º que nenhum aberto ocupa (fechado o 1.º de dois, o novo não cai em cima do 2.º).
    public static int FreeSlot(IEnumerable<int> used)
    {
        var taken = used.ToHashSet();
        var slot = 0;
        while (taken.Contains(slot)) slot++;
        return slot;
    }
}
