namespace Eteris.Browser.Tabs;

// Para onde uma aba arrastada pode ser largada para criar o ecrã dividido.
public enum SplitDropTarget { None, Right, Bottom }

// Arrastar uma aba até à margem da página: a faixa dos últimos SplitDropZone.Width pixels à direita cria a divisão
// lado a lado (a aba arrastada fica à direita); a faixa de baixo cria-a empilhada (a aba arrastada fica em baixo).
public static class SplitDropZone
{
    public const double Width = 24;

    // x, y: posição do rato em relação à área da página (largura w, altura h). slackRight/slackBottom: o que há
    // entre a página e a orla da janela (moldura e, maximizada, a margem fora do ecrã) — atirar o rato ao bordo do
    // ecrã, que é onde ele para, continua a contar como margem.
    public static SplitDropTarget TargetAt(double x, double y, double w, double h, double slackRight = 0, double slackBottom = 0)
    {
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x > w + Math.Max(0, slackRight) || y > h + Math.Max(0, slackBottom))
            return SplitDropTarget.None;
        if (x >= w - Width) return SplitDropTarget.Right;
        if (y >= h - Width) return SplitDropTarget.Bottom;
        return SplitDropTarget.None;
    }

    public static SplitOrientation OrientationOf(SplitDropTarget target)
        => target == SplitDropTarget.Bottom ? SplitOrientation.Stacked : SplitOrientation.SideBySide;

    // A divisão junta a aba que estava ativa antes de arrastar com a arrastada; arrastar a própria aba ativa (ou
    // sem outra aba) não divide nada.
    public static bool CanSplit(BrowserTab? activeBeforeDrag, BrowserTab? dragged)
        => activeBeforeDrag is not null && dragged is not null && !ReferenceEquals(activeBeforeDrag, dragged);
}
