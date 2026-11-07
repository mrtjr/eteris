namespace Eteris.Browser.Tabs;

// Geometria da faixa de abas do topo. Com muitas abas a faixa não encolhe as abas até ficarem ilegíveis: cada aba
// tem uma largura mínima legível e a faixa passa a rolar (roda do rato, setas nas pontas, aba ativa sempre à vista).
public static class TabLayout
{
    public const double MaxTabWidth = 232;
    // Ícone + umas 10 letras do título + «x»: abaixo disto o título já não se lê (o Chrome encolhe até ao ícone).
    public const double MinTabWidth = 120;
    // Largura de cada seta de rolagem nas pontas da faixa.
    public const double ArrowWidth = 24;
    // Quanto a faixa anda por cada passo da roda do rato ou clique numa seta.
    public const double ScrollStep = 160;
    // A arrastar uma aba junto a uma ponta da faixa, a faixa rola sozinha nesta orla.
    public const double DragEdge = 36;

    // Posição horizontal (relativa ao início das abas) → índice onde a aba arrastada deve ficar.
    public static int IndexAt(double x, double tabWidth, int count)
        => count <= 0 ? 0 : Math.Clamp((int)Math.Floor(x / tabWidth), 0, count - 1);

    public static double TabWidth(double available, int count)
    {
        if (count <= 0) return MaxTabWidth;
        return Math.Clamp(Math.Floor(available / count), MinTabWidth, MaxTabWidth);
    }

    // As abas, à largura mínima, já não cabem na faixa: há que rolar.
    public static bool Overflows(double available, int count)
        => count > 0 && count * MinTabWidth > available;

    // Deslocamento que deixa a aba [tabLeft, tabLeft+tabWidth) inteira à vista, mexendo o menos possível
    // (como o ScrollIntoView): nada muda se já estiver visível.
    public static double RevealOffset(double offset, double tabLeft, double tabWidth, double viewport, double extent)
    {
        var max = Math.Max(0, extent - viewport);
        var target = offset;
        if (tabLeft < offset) target = tabLeft;
        else if (tabLeft + tabWidth > offset + viewport) target = tabLeft + tabWidth - viewport;
        return Math.Clamp(target, 0, max);
    }

    // Que setas mostrar: só quando há mais abas para esse lado.
    public static (bool Left, bool Right) Arrows(double offset, double viewport, double extent)
    {
        var max = extent - viewport;
        if (max <= 0.5) return (false, false);
        return (offset > 0.5, offset < max - 0.5);
    }

    // A arrastar junto à orla: −1 rola para a esquerda, +1 para a direita, 0 nada.
    public static int DragScrollDirection(double xInViewport, double viewport)
        => xInViewport < DragEdge ? -1 : xInViewport > viewport - DragEdge ? 1 : 0;

    // Deslocamento depois de um passo da roda ou de uma seta, dentro dos limites.
    public static double Step(double offset, int direction, double viewport, double extent)
        => Math.Clamp(offset + direction * ScrollStep, 0, Math.Max(0, extent - viewport));
}
