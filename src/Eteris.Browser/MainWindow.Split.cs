using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Ecrã dividido: duas abas lado a lado ou empilhadas. A aba focada é a ativa (omnibox, escudo, zoom seguem-na).
public partial class MainWindow
{
    private SplitView? _split;
    private GridSplitter? _splitter;
    private Border? _focusRing;
    private double _splitRatio = 0.5;

    private bool IsSplit => _split is not null;

    private void OpenSideBySide(BrowserTab other, SplitOrientation? orientation = null)
    {
        if (Tabs.Active is not { } active || ReferenceEquals(active, other)) return;
        if (other.IsSleeping)
        {
            other.IsSleeping = false;
            CreateBrowser(other);
        }
        _split = new SplitView(active, other, orientation ?? _split?.Orientation ?? SplitOrientation.SideBySide);
        LayoutBrowsers();
        OnSplitChanged();
        ShowToast(_split.IsStacked ? "Ecrã dividido (empilhado) — clique numa página para a tornar ativa"
            : "Ecrã dividido — clique numa página para a tornar ativa");
    }

    private void CloseSplit()
    {
        if (_split is not { } split) return;
        EndSplit(split);
        LayoutBrowsers();
        OnSplitChanged();
    }

    // «Trocar lados»: a da esquerda passa para a direita (ou a de cima para baixo), sem mexer nas abas.
    private void SwapSplit()
    {
        if (_split is not { } split) return;
        split.Swap();
        _splitRatio = SplitView.ClampRatio(1 - _splitRatio);
        RebuildSplitTracks();
        OnSplitChanged();
    }

    // «Empilhar» / «Lado a lado».
    private void ToggleSplitOrientation()
    {
        if (_split is not { } split) return;
        split.ToggleOrientation();
        RebuildSplitTracks();
        OnSplitChanged();
        ShowToast(split.IsStacked ? "Páginas empilhadas" : "Páginas lado a lado");
    }

    // Chamado sempre que a divisão nasce, muda ou acaba: selo na barra de endereço e sessão.
    private void OnSplitChanged()
    {
        UpdateSplitBadge();
        ScheduleSessionSave();
    }

    // As duas abas estiveram no ecrã até agora: contam como vistas, senão a que nunca foi ativa adormece no minuto
    // seguinte (SleepIdleTabs só a protegia enquanto estava num painel).
    private void EndSplit(SplitView split)
    {
        var now = DateTime.UtcNow;
        split.Left.LastActiveUtc = now;
        split.Right.LastActiveUtc = now;
        _split = null;
    }

    // Aba vizinha para a paleta: a da direita, ou a da esquerda se a ativa for a última.
    private BrowserTab? SplitCandidate()
    {
        if (Tabs.Active is not { } active) return null;
        var index = Tabs.Tabs.IndexOf(active);
        return index + 1 < Tabs.Tabs.Count ? Tabs.Tabs[index + 1] : index > 0 ? Tabs.Tabs[index - 1] : null;
    }

    private void OnSplitActiveChanged()
    {
        var was = IsSplit;
        if (_split is { } split && !split.SurvivesActivation(Tabs.Active)) EndSplit(split);
        LayoutBrowsers();
        if (was != IsSplit) OnSplitChanged();
    }

    private void OnSplitTabRemoved(BrowserTab tab)
    {
        if (_split is { } split && !split.SurvivesRemoval(tab))
        {
            EndSplit(split);
            LayoutBrowsers();
            OnSplitChanged();
        }
    }

    // As faixas (colunas ou linhas) da grelha são refeitas do zero: a orientação ou a proporção mudaram.
    private void RebuildSplitTracks()
    {
        BrowserHost.ColumnDefinitions.Clear();
        BrowserHost.RowDefinitions.Clear();
        LayoutBrowsers();
    }

    private void LayoutBrowsers()
    {
        EnsureSplitChrome();
        var columns = BrowserHost.ColumnDefinitions;
        var rows = BrowserHost.RowDefinitions;
        if (_split is null)
        {
            columns.Clear();
            rows.Clear();
        }
        else if (_split.IsStacked && rows.Count != 3)
        {
            columns.Clear();
            rows.Clear();
            rows.Add(new RowDefinition { Height = new GridLength(_splitRatio, GridUnitType.Star), MinHeight = 160 });
            rows.Add(new RowDefinition { Height = new GridLength(8) });
            rows.Add(new RowDefinition { Height = new GridLength(1 - _splitRatio, GridUnitType.Star), MinHeight = 160 });
        }
        else if (!_split.IsStacked && columns.Count != 3)
        {
            rows.Clear();
            columns.Clear();
            columns.Add(new ColumnDefinition { Width = new GridLength(_splitRatio, GridUnitType.Star), MinWidth = 220 });
            columns.Add(new ColumnDefinition { Width = new GridLength(8) });
            columns.Add(new ColumnDefinition { Width = new GridLength(1 - _splitRatio, GridUnitType.Star), MinWidth = 220 });
        }

        foreach (var (tab, browser) in _browsers.Select(p => (p.Key, (FrameworkElement)p.Value))
                     .Concat(_videoViews.Select(p => (p.Key, (FrameworkElement)p.Value))))
        {
            var pane = _split?.PaneOf(tab);
            var visible = pane is not null || (_split is null && ReferenceEquals(tab, Tabs.Active));
            browser.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (browser is ChromiumWebBrowser cef && cef.IsBrowserInitialized) SetPageVisible(tab, cef, visible);
            PlaceInHost(browser, pane);
        }

        PlaceInHost(AuroraBar, null);
        var stacked = _split?.IsStacked == true;
        _splitter!.Visibility = _split is null ? Visibility.Collapsed : Visibility.Visible;
        _splitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        _splitter.Width = stacked ? double.NaN : 8;
        _splitter.Height = stacked ? 8 : double.NaN;
        _splitter.Cursor = stacked ? Cursors.SizeNS : Cursors.SizeWE;
        Grid.SetColumn(_splitter, stacked ? 0 : 1);
        Grid.SetRow(_splitter, stacked ? 1 : 0);
        Grid.SetColumnSpan(_splitter, stacked ? 3 : 1);
        Grid.SetRowSpan(_splitter, stacked ? 1 : 3);

        _focusRing!.Visibility = _split is null ? Visibility.Collapsed : Visibility.Visible;
        if (_split?.PaneOf(Tabs.Active) is { } activePane) PlaceInHost(_focusRing, activePane);
    }

    // Posição de um elemento na grelha da página: uma metade (coluna ou linha conforme a orientação) ou a área toda.
    private void PlaceInHost(UIElement element, SplitPane? pane)
    {
        var stacked = _split?.IsStacked == true;
        var second = pane == SplitPane.Right;
        Grid.SetColumn(element, !stacked && second ? 2 : 0);
        Grid.SetRow(element, stacked && second ? 2 : 0);
        Grid.SetColumnSpan(element, pane is null || stacked ? 3 : 1);
        Grid.SetRowSpan(element, pane is null || !stacked ? 3 : 1);
    }

    private void EnsureSplitChrome()
    {
        if (_splitter is not null) return;
        _splitter = new GridSplitter
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = (Brush)Resources["ChromeSurfaceBrush"],
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = Cursors.SizeWE,
            Visibility = Visibility.Collapsed,
            ToolTip = "Arraste para ajustar · duplo clique para 50/50",
        };
        Grid.SetColumn(_splitter, 1);
        Panel.SetZIndex(_splitter, 20);
        _splitter.DragCompleted += (_, _) =>
        {
            var columns = BrowserHost.ColumnDefinitions;
            var rows = BrowserHost.RowDefinitions;
            if (_split?.IsStacked == true && rows.Count == 3)
                _splitRatio = SplitView.ClampRatio(rows[0].ActualHeight / (rows[0].ActualHeight + rows[2].ActualHeight));
            else if (columns.Count == 3)
                _splitRatio = SplitView.ClampRatio(columns[0].ActualWidth / (columns[0].ActualWidth + columns[2].ActualWidth));
        };
        _splitter.MouseDoubleClick += (_, _) =>
        {
            _splitRatio = 0.5;
            RebuildSplitTracks();
        };
        BrowserHost.Children.Add(_splitter);

        // Contorno íris à volta da página ativa.
        _focusRing = new Border
        {
            BorderBrush = (Brush)FindResource("IrisBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Opacity = 0.85,
            Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(_focusRing, 15);
        BrowserHost.Children.Add(_focusRing);
    }

    // Selo «Dividido» / «Empilhado» na barra de endereço, com o menu das ações do ecrã dividido.
    private void UpdateSplitBadge()
    {
        SplitBadge.Visibility = IsSplit ? Visibility.Visible : Visibility.Collapsed;
        SplitBadgeText.Text = _split?.IsStacked == true ? "Empilhado" : "Dividido";
    }

    private void SplitBadge_Click(object sender, RoutedEventArgs e)
    {
        if (_split is not { } split) return;
        new Infrastructure.DarkMenu()
            .Item("", "Trocar lados", SwapSplit)
            .Item(split.IsStacked ? "" : "", split.IsStacked ? "Lado a lado" : "Empilhar", ToggleSplitOrientation)
            .Separator()
            .Item("", "Desfazer (fica a página ativa)", CloseSplit)
            .OpenBelow(SplitBadge);
    }

    // Clicar numa das metades torna-a ativa.
    private void WatchPaneFocus(BrowserTab tab, UIElement browser)
        => browser.PreviewMouseDown += (_, _) =>
        {
            if (IsSplit && !ReferenceEquals(Tabs.Active, tab) && _split!.PaneOf(tab) is not null) Tabs.Active = tab;
        };
}
