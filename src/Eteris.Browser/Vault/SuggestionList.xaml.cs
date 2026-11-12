using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Eteris.Browser.Vault;

// As linhas da lista de sugestões: a inicial do site, o utilizador e de onde é (nunca a palavra-passe). O rato escolhe
// uma linha ao largar o botão; o teclado (↑ ↓ Enter) chega pela janela. Nada aqui recebe o foco.
public partial class SuggestionList : UserControl
{
    private readonly List<(SuggestionRow Row, SuggestionRowElement Element)> _rows = [];
    private Point? _lastMouse;
    // A linha onde o botão do rato desceu e quando (UTC): só um clique que começa e acaba na mesma linha escolhe.
    private (SuggestionRowElement Element, DateTime At)? _pressed;
    // Onde o cursor estava (píxeis do ecrã) quando a lista apareceu e se saiu de uma linha depois disso: uma lista que
    // nasce debaixo de um cursor parado só aceita o clique depois de o cursor se ter mexido (VAU-04).
    private Point? _cursorAtShow;
    private bool _cursorLeft;

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

    // A linha escolhida com o rato (índice) e quando o botão desceu nela (UTC).
    public event Action<int, DateTime>? Chosen;

    public event Action? ManageRequested;

    public int Selected { get; private set; } = -1;

    // O rodapé («Gerir palavras-passe…», «Gerir pagamentos…»…).
    public string Footer
    {
        get => ManageText.Text;
        set
        {
            ManageText.Text = value;
            AutomationProperties.SetName(Manage, value.TrimEnd('…'));
        }
    }

    // O nome da lista para o leitor de ecrã («Palavras-passe guardadas», «Palavra-passe forte»…).
    public string Heading
    {
        get => AutomationProperties.GetName(this);
        set => AutomationProperties.SetName(this, value);
    }

    public int Count => _rows.Count;

    public SuggestionRow? SelectedRow => Selected >= 0 && Selected < _rows.Count ? _rows[Selected].Row : null;

    public void Show(IReadOnlyList<SuggestionRow> rows, int selected)
    {
        // O primeiro movimento depois de aparecer só marca onde o cursor está (pode ser o WPF a avisar que a lista
        // apareceu por baixo dele).
        _lastMouse = null;
        _pressed = null;
        _cursorAtShow = CursorOnScreen();
        _cursorLeft = false;
        Rows.Children.Clear();
        _rows.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            var element = Build(rows[i], i, rows.Count);
            _rows.Add((rows[i], element));
            Rows.Children.Add(element);
        }
        Select(selected, announce: false);
        // O foco do teclado fica no campo da página: o leitor de ecrã sabe da lista por um anúncio.
        Announce(selected >= 0 && selected < rows.Count ? Announcement(rows[selected], selected, rows.Count) : Opening(Heading, rows.Count));
    }

    // O que o leitor de ecrã diz ao abrir a lista e ao mudar de linha (puro).
    public static string Opening(string heading, int count)
        => $"{heading}: {(count == 1 ? "1 sugestão" : $"{count} sugestões")}. Seta para baixo para escolher, Enter para preencher.";

    public static string Announcement(SuggestionRow row, int index, int count)
        => (row.Detail.Length > 0 ? $"{row.Title}, {row.Detail}" : row.Title) + $", {index + 1} de {count}";

    private void Announce(string text)
    {
        try
        {
            UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseNotificationEvent(AutomationNotificationKind.ItemAdded,
                AutomationNotificationProcessing.ImportantMostRecent, text, "eteris-sugestoes");
        }
        catch (InvalidOperationException) { }
    }

    // ↑ e ↓, com volta.
    public void Move(int delta)
    {
        if (_rows.Count == 0) return;
        Select(Selected < 0 ? (delta > 0 ? 0 : _rows.Count - 1) : ((Selected + delta) % _rows.Count + _rows.Count) % _rows.Count);
    }

    public void Select(int index) => Select(index, announce: true);

    private void Select(int index, bool announce)
    {
        var before = Selected;
        Selected = index >= 0 && index < _rows.Count ? index : -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Element.Background = i == Selected ? (Brush)FindResource("SelectedBrush") : Brushes.Transparent;
            _rows[i].Element.IsChosen = i == Selected;
            AutomationProperties.SetItemStatus(_rows[i].Element, i == Selected ? "escolhida" : string.Empty);
        }
        if (!announce || Selected < 0 || Selected == before) return;
        if (UIElementAutomationPeer.FromElement(_rows[Selected].Element) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        Announce(Announcement(_rows[Selected].Row, Selected, _rows.Count));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ListPeer(this);

    // A lista é uma «lista» para o leitor de ecrã (como a do Chrome), com o nome do cabeçalho.
    private sealed class ListPeer(SuggestionList owner) : UserControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => "SugestoesDoCofre";
    }

    private SuggestionRowElement Build(SuggestionRow row, int index, int count)
    {
        var initial = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        initial.Children.Add(new Ellipse { Fill = (Brush)FindResource(row.Insecure ? "DangerBrush" : "IrisBrush"), Opacity = 0.18 });
        initial.Children.Add(new TextBlock
        {
            Text = row.Glyph ?? Initial(row),
            FontFamily = row.Glyph is null ? FontFamily : (FontFamily)FindResource("IconFont"),
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
        var element = new SuggestionRowElement { Padding = new Thickness(8, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid };
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
        // O cursor só «saiu» se a linha ainda é desta lista e ele andou pelo menos PointerSlack desde que a lista
        // apareceu: uma lista que sai de baixo de um cursor parado (ou se refaz) não conta.
        element.MouseLeave += (_, _) =>
        {
            if (!_rows.Exists(r => ReferenceEquals(r.Element, element))) return;
            if (_cursorAtShow is { } at && CursorOnScreen() is { } now && VaultFillPolicy.CountsAsLeaving((now - at).Length))
                _cursorLeft = true;
        };
        element.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _pressed = (element, DateTime.UtcNow);
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_pressed is not { } pressed || !ReferenceEquals(pressed.Element, element)) return;
            _pressed = null;
            // A linha estava debaixo do cursor quando a lista apareceu e o cursor ainda não saiu dela: o clique é
            // do que estava ali antes (um rótulo da página clicado duas vezes), não uma escolha.
            var held = _cursorAtShow is { } p && RowRectOnScreen(element) is { } rect && rect.Contains(p);
            if (!VaultFillPolicy.AcceptsPointer(held, _cursorLeft)) return;
            var i = _rows.FindIndex(r => ReferenceEquals(r.Element, element));
            if (i >= 0) Chosen?.Invoke(i, pressed.At);
        };
        return element;
    }

    private static Rect? RowRectOnScreen(SuggestionRowElement element)
    {
        try
        {
            if (PresentationSource.FromVisual(element) is null) return null;
            return new Rect(element.PointToScreen(new Point(0, 0)), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        }
        catch (InvalidOperationException) { return null; }
    }

    // O cursor em píxeis do ecrã: o WPF não recebe os movimentos do rato sobre a página (mesmo padrão de MainWindow.AutoHide).
    private static Point? CursorOnScreen() => GetCursorPos(out var p) ? new Point(p.X, p.Y) : null;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    // Em píxeis do ecrã: a lista mexe-se (aparece noutro campo, muda de tamanho) sem o cursor se mexer.
    private Point? Screen(Point local)
    {
        try { return PresentationSource.FromVisual(this) is null ? null : PointToScreen(local); }
        catch (InvalidOperationException) { return null; }
    }

    internal static string InitialOf(SuggestionRow row) => Initial(row);

    private static string Initial(SuggestionRow row)
    {
        var source = row.Title == VaultFillPolicy.NoUsername ? row.Detail : row.Title;
        return source.Length > 0 ? source[..1].ToUpperInvariant() : "?";
    }
}

// Uma linha da lista para o leitor de ecrã: um «item de lista» com o nome, a posição «n de N» e se está escolhida
// (SelectionItem). Um Border simples não tem AutomationPeer e nada disto chegava à UIA.
public sealed class SuggestionRowElement : Border
{
    public bool IsChosen
    {
        get => _chosen;
        set
        {
            if (_chosen == value) return;
            _chosen = value;
            if (UIElementAutomationPeer.FromElement(this) is { } peer)
                peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !value, value);
        }
    }

    private bool _chosen;

    protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

    private sealed class RowPeer(SuggestionRowElement owner) : FrameworkElementAutomationPeer(owner), ISelectionItemProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => "SugestaoDoCofre";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;

        public override object GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);

        public bool IsSelected => owner.IsChosen;

        public IRawElementProviderSimple? SelectionContainer
            => owner.Parent is UIElement parent && CreatePeerForElement(parent) is { } container ? ProviderFromPeer(container) : null;

        // A escolha faz-se com as setas (no campo da página) ou com o rato: o leitor de ecrã só a lê.
        public void Select() { }
        public void AddToSelection() { }
        public void RemoveFromSelection() { }
    }
}
