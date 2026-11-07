using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Ecrã dividido: duas abas lado a lado. A aba focada é a ativa (omnibox, escudo, zoom seguem-na).
public partial class MainWindow
{
    private SplitView? _split;
    private GridSplitter? _splitter;
    private Border? _focusRing;
    private double _splitRatio = 0.5;

    private bool IsSplit => _split is not null;

    private void OpenSideBySide(BrowserTab other)
    {
        if (Tabs.Active is not { } active || ReferenceEquals(active, other)) return;
        if (other.IsSleeping)
        {
            other.IsSleeping = false;
            CreateBrowser(other);
        }
        _split = new SplitView(active, other);
        LayoutBrowsers();
        ShowToast("Ecrã dividido — clique numa página para a tornar ativa");
    }

    private void CloseSplit()
    {
        if (_split is not { } split) return;
        EndSplit(split);
        LayoutBrowsers();
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
        if (_split is { } split && !split.SurvivesActivation(Tabs.Active)) EndSplit(split);
        LayoutBrowsers();
    }

    private void OnSplitTabRemoved(BrowserTab tab)
    {
        if (_split is { } split && !split.SurvivesRemoval(tab))
        {
            EndSplit(split);
            LayoutBrowsers();
        }
    }

    private void LayoutBrowsers()
    {
        EnsureSplitChrome();
        var columns = BrowserHost.ColumnDefinitions;
        if (_split is null)
        {
            columns.Clear();
        }
        else if (columns.Count != 3)
        {
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
            Grid.SetColumn(browser, pane == SplitPane.Right ? 2 : 0);
            Grid.SetColumnSpan(browser, pane is null ? 3 : 1);
        }

        Grid.SetColumnSpan(AuroraBar, 3);
        _splitter!.Visibility = _split is null ? Visibility.Collapsed : Visibility.Visible;

        _focusRing!.Visibility = _split is null ? Visibility.Collapsed : Visibility.Visible;
        if (_split?.PaneOf(Tabs.Active) is { } activePane) Grid.SetColumn(_focusRing, activePane == SplitPane.Right ? 2 : 0);
    }

    private void EnsureSplitChrome()
    {
        if (_splitter is not null) return;
        _splitter = new GridSplitter
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
            if (columns.Count == 3)
                _splitRatio = SplitView.ClampRatio(columns[0].ActualWidth / (columns[0].ActualWidth + columns[2].ActualWidth));
        };
        _splitter.MouseDoubleClick += (_, _) =>
        {
            _splitRatio = 0.5;
            BrowserHost.ColumnDefinitions.Clear();
            LayoutBrowsers();
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

    // Clicar numa das metades torna-a ativa.
    private void WatchPaneFocus(BrowserTab tab, UIElement browser)
        => browser.PreviewMouseDown += (_, _) =>
        {
            if (IsSplit && !ReferenceEquals(Tabs.Active, tab) && _split!.PaneOf(tab) is not null) Tabs.Active = tab;
        };
}
