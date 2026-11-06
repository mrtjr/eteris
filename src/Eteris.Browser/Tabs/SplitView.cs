namespace Eteris.Browser.Tabs;

public enum SplitPane { Left, Right }

// Duas abas lado a lado. Mudar entre elas mantém a divisão; abrir outra aba ou fechar uma delas desfaz-a.
public sealed class SplitView
{
    public const double MinRatio = 0.2;
    public const double MaxRatio = 0.8;

    public SplitView(BrowserTab left, BrowserTab right)
    {
        if (ReferenceEquals(left, right)) throw new ArgumentException("Uma aba não pode ficar ao lado de si própria.");
        Left = left;
        Right = right;
    }

    public BrowserTab Left { get; }
    public BrowserTab Right { get; }

    public SplitPane? PaneOf(BrowserTab? tab)
        => ReferenceEquals(tab, Left) ? SplitPane.Left : ReferenceEquals(tab, Right) ? SplitPane.Right : null;

    public BrowserTab? PartnerOf(BrowserTab tab)
        => ReferenceEquals(tab, Left) ? Right : ReferenceEquals(tab, Right) ? Left : null;

    public bool SurvivesActivation(BrowserTab? active) => PaneOf(active) is not null;

    public bool SurvivesRemoval(BrowserTab removed) => PaneOf(removed) is null;

    public static double ClampRatio(double ratio) => Math.Clamp(ratio, MinRatio, MaxRatio);
}
