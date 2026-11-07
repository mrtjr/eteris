namespace Eteris.Browser.Tabs;

// A metade onde está uma aba: a primeira (esquerda, ou em cima quando empilhado) ou a segunda (direita / em baixo).
public enum SplitPane { Left, Right }

public enum SplitOrientation { SideBySide, Stacked }

// Duas abas no mesmo ecrã, lado a lado ou empilhadas. Mudar entre elas mantém a divisão; abrir outra aba ou fechar
// uma delas desfaz-a. «Trocar lados» inverte as metades sem mexer nas abas.
public sealed class SplitView
{
    public const double MinRatio = 0.2;
    public const double MaxRatio = 0.8;

    public SplitView(BrowserTab left, BrowserTab right, SplitOrientation orientation = SplitOrientation.SideBySide)
    {
        if (ReferenceEquals(left, right)) throw new ArgumentException("Uma aba não pode ficar ao lado de si própria.");
        Left = left;
        Right = right;
        Orientation = orientation;
    }

    public BrowserTab Left { get; private set; }
    public BrowserTab Right { get; private set; }
    public SplitOrientation Orientation { get; set; }

    public bool IsStacked => Orientation == SplitOrientation.Stacked;

    public SplitPane? PaneOf(BrowserTab? tab)
        => ReferenceEquals(tab, Left) ? SplitPane.Left : ReferenceEquals(tab, Right) ? SplitPane.Right : null;

    public BrowserTab? PartnerOf(BrowserTab tab)
        => ReferenceEquals(tab, Left) ? Right : ReferenceEquals(tab, Right) ? Left : null;

    public bool SurvivesActivation(BrowserTab? active) => PaneOf(active) is not null;

    public bool SurvivesRemoval(BrowserTab removed) => PaneOf(removed) is null;

    public void Swap() => (Left, Right) = (Right, Left);

    public void ToggleOrientation()
        => Orientation = IsStacked ? SplitOrientation.SideBySide : SplitOrientation.Stacked;

    public static double ClampRatio(double ratio) => Math.Clamp(ratio, MinRatio, MaxRatio);

    // Nome gravado na sessão («stacked» / «side»); qualquer outra coisa é lado a lado.
    public static string Name(SplitOrientation orientation) => orientation == SplitOrientation.Stacked ? "stacked" : "side";

    public static SplitOrientation ParseOrientation(string? name)
        => string.Equals(name, "stacked", StringComparison.OrdinalIgnoreCase) ? SplitOrientation.Stacked : SplitOrientation.SideBySide;
}
