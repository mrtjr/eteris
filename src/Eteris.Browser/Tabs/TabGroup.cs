using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Eteris.Browser.Tabs;

// Grupo de abas (nome e cor), como no Chrome. As abas do grupo ficam sempre juntas na faixa.
public sealed partial class TabGroup : ObservableObject
{
    public static readonly string[] Palette = ["#8B5CF6", "#22D3EE", "#34D399", "#F472B6", "#F59E0B", "#3B82F6", "#EF4444", "#94A3B8"];
    public static readonly string[] PaletteNames = ["Violeta", "Ciano", "Verde", "Rosa", "Âmbar", "Azul", "Vermelho", "Cinza"];

    private static readonly Brush[] Brushes = Palette
        .Select(hex => { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; })
        .ToArray();

    public TabGroup(string name, int colorIndex)
    {
        _name = name;
        _colorIndex = colorIndex;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    private int _colorIndex;

    [ObservableProperty]
    private bool _isCollapsed;

    public Brush ColorBrush => BrushAt(ColorIndex);

    // A mesma paleta serve a cor do ícone de uma aba (Tabs/TabIdentity.cs).
    public static Brush BrushAt(int index) => Brushes[((index % Brushes.Length) + Brushes.Length) % Brushes.Length];

    public static Color ColorAt(int index) => ((SolidColorBrush)BrushAt(index)).Color;

    public IEnumerable<BrowserTab> Members(TabsViewModel tabs) => tabs.Tabs.Where(t => ReferenceEquals(t.Group, this));
}
