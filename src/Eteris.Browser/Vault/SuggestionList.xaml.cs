using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Eteris.Browser.Vault;

// As linhas da lista de sugestões: a inicial do site, o utilizador e de onde é (nunca a palavra-passe). O rato escolhe
// uma linha ao largar o botão; o teclado (↑ ↓ Enter) chega pela janela. Nada aqui recebe o foco.
public partial class SuggestionList : UserControl
{
    private readonly List<(SuggestionRow Row, Border Element)> _rows = [];
    private Point? _lastMouse;

    public SuggestionList()
    {
        InitializeComponent();
        Manage.MouseEnter += (_, _) => Manage.Background = (Brush)FindResource("HoverBrush");
        Manage.MouseLeave += (_, _) => Manage.Background = Brushes.Transparent;
        Manage.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Manage.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ManageRequested?.Invoke();
        };
    }

    // A linha escolhida com o rato (índice).
    public event Action<int>? Chosen;

    public event Action? ManageRequested;

    public int Selected { get; private set; } = -1;

    public int Count => _rows.Count;

    public SuggestionRow? SelectedRow => Selected >= 0 && Selected < _rows.Count ? _rows[Selected].Row : null;

    public void Show(IReadOnlyList<SuggestionRow> rows, int selected)
    {
        // O primeiro movimento depois de aparecer só marca onde o cursor está (pode ser o WPF a avisar que a lista
        // apareceu por baixo dele).
        _lastMouse = null;
        Rows.Children.Clear();
        _rows.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            var element = Build(rows[i], i, rows.Count);
            _rows.Add((rows[i], element));
            Rows.Children.Add(element);
        }
        Select(selected);
    }

    // ↑ e ↓, com volta.
    public void Move(int delta)
    {
        if (_rows.Count == 0) return;
        Select(Selected < 0 ? (delta > 0 ? 0 : _rows.Count - 1) : ((Selected + delta) % _rows.Count + _rows.Count) % _rows.Count);
    }

    public void Select(int index)
    {
        Selected = index >= 0 && index < _rows.Count ? index : -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Element.Background = i == Selected ? (Brush)FindResource("SelectedBrush") : Brushes.Transparent;
            AutomationProperties.SetItemStatus(_rows[i].Element, i == Selected ? "escolhida" : string.Empty);
        }
    }

    private Border Build(SuggestionRow row, int index, int count)
    {
        var initial = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        initial.Children.Add(new Ellipse { Fill = (Brush)FindResource(row.Insecure ? "DangerBrush" : "IrisBrush"), Opacity = 0.18 });
        initial.Children.Add(new TextBlock
        {
            Text = Initial(row),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource(row.Insecure ? "DangerBrush" : "IrisBrush"),
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = row.Detail,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource(row.Insecure ? "DangerBrush" : "TextMutedBrush"),
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(initial);
        grid.Children.Add(text);
        var element = new Border { Padding = new Thickness(8, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid };
        AutomationProperties.SetName(element, $"{row.Title}, {row.Detail}");
        AutomationProperties.SetPositionInSet(element, index + 1);
        AutomationProperties.SetSizeOfSet(element, count);
        // Só um rato que se mexe escolhe: a lista a aparecer (ou a mudar) por baixo de um cursor parado não tira a
        // escolha feita com as setas.
        element.MouseMove += (_, e) =>
        {
            if (Screen(e.GetPosition(this)) is not { } at) return;
            var moved = _lastMouse is { } last && (last - at).Length >= 1;
            _lastMouse = at;
            if (!moved) return;
            Select(_rows.FindIndex(r => ReferenceEquals(r.Element, element)));
        };
        element.MouseLeftButtonDown += (_, e) => e.Handled = true;
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var i = _rows.FindIndex(r => ReferenceEquals(r.Element, element));
            if (i >= 0) Chosen?.Invoke(i);
        };
        return element;
    }

    // Em píxeis do ecrã: a lista mexe-se (aparece noutro campo, muda de tamanho) sem o cursor se mexer.
    private Point? Screen(Point local)
    {
        try { return PresentationSource.FromVisual(this) is null ? null : PointToScreen(local); }
        catch (InvalidOperationException) { return null; }
    }

    private static string Initial(SuggestionRow row)
    {
        var source = row.Title == VaultFillPolicy.NoUsername ? row.Detail : row.Title;
        return source.Length > 0 ? source[..1].ToUpperInvariant() : "?";
    }
}
