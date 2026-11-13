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
    private double _splitRatio = 0.5;

    private bool IsSplit => _split is not null;

    private void OpenSideBySide(BrowserTab other, SplitOrientation? orientation = null, bool announce = true)
    {
        if (Tabs.Active is not { } active || ReferenceEquals(active, other)) return;
        _pendingSplit = null;
        if (other.IsSleeping)
        {
            other.IsSleeping = false;
            CreateBrowser(other);
        }
        _split = new SplitView(active, other, orientation ?? _split?.Orientation ?? SplitOrientation.SideBySide);
        LayoutBrowsers();
        OnSplitChanged();
        if (announce)
            ShowToast(_split.IsStacked ? "Ecrã dividido (empilhado) — clique numa página para a tornar ativa"
                : "Ecrã dividido — clique numa página para a tornar ativa");
    }

    // Divisão reposta da sessão que ainda não está no ecrã: o plano de arranque (Novidades, um link vindo de outra
    // aplicação) ativou outra aba. Fica guardada na sessão e volta quando uma das duas abas for ativada.
    private PendingSplit? _pendingSplit;

    private sealed record PendingSplit(BrowserTab Left, BrowserTab Right, SplitOrientation? Orientation);

    // A divisão gravada na sessão: índices das duas abas e a orientação. null quando não há divisão.
    private Data.SessionSplit? SplitSnapshot()
    {
        var (first, second, orientation) = _split is { } split ? (split.Left, split.Right, split.Orientation)
            : _pendingSplit is { } pending ? (pending.Left, pending.Right, pending.Orientation ?? SplitOrientation.SideBySide)
            : ((BrowserTab?)null, (BrowserTab?)null, SplitOrientation.SideBySide);
        if (first is null || second is null) return null;
        var (left, right) = (Tabs.Tabs.IndexOf(first), Tabs.Tabs.IndexOf(second));
        return left >= 0 && right >= 0 ? new Data.SessionSplit(left, right, SplitView.Name(orientation)) : null;
    }

    private PendingSplit? SplitTabs(Data.SessionSplit? saved)
        => saved is not null && saved.IsValid(Tabs.Tabs.Count)
            ? new PendingSplit(Tabs.Tabs[saved.Left], Tabs.Tabs[saved.Right], SplitView.ParseOrientation(saved.Orientation))
            : null;

    // Ao voltar a um Espaço: a divisão dele volta como estava.
    private void RestoreSplit(Data.SessionSplit? saved)
    {
        if (SplitTabs(saved) is { } split) RestoreSplit(split);
    }

    // Ao arrancar com a sessão anterior: a divisão volta como estava (as duas abas acordam), com a mesma aba ativa —
    // ou fica à espera, se o plano de arranque ativou outra aba (senão desfazia-se logo e perdia-se ao gravar).
    private void RestoreSplitAtStartup(PendingSplit saved, bool startupActivatedATab)
    {
        if (startupActivatedATab && !ReferenceEquals(Tabs.Active, saved.Left) && !ReferenceEquals(Tabs.Active, saved.Right))
            _pendingSplit = saved;
        else RestoreSplit(saved);
    }

    private void RestoreSplit(PendingSplit saved)
    {
        _pendingSplit = null;
        if (Tabs.Active is not { } active || !Tabs.Tabs.Contains(saved.Left) || !Tabs.Tabs.Contains(saved.Right)) return;
        var (left, right) = (saved.Left, saved.Right);
        if (ReferenceEquals(active, right)) (left, right) = (right, left);
        else if (!ReferenceEquals(active, left)) Tabs.Active = left;
        OpenSideBySide(right, saved.Orientation, announce: false);
        // A ordem das metades é a gravada, mesmo que a ativa fosse a da direita.
        if (_split is { } split && !ReferenceEquals(split.Left, saved.Left)) SwapSplit();
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
        // A outra metade passou a estar à vista: o pedido de certificado que ela tinha à espera aparece.
        ShowNextClientCertificate();
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
        // A divisão à espera volta quando uma das suas abas fica ativa.
        if (_split is null && _pendingSplit is { } pending
            && (ReferenceEquals(Tabs.Active, pending.Left) || ReferenceEquals(Tabs.Active, pending.Right)))
        {
            RestoreSplit(pending);
            return;
        }
        var was = IsSplit;
        if (_split is { } split && !split.SurvivesActivation(Tabs.Active)) EndSplit(split);
        LayoutBrowsers();
        if (was != IsSplit) OnSplitChanged();
    }

    private void OnSplitTabRemoved(BrowserTab tab)
    {
        if (_pendingSplit is { } pending && (ReferenceEquals(pending.Left, tab) || ReferenceEquals(pending.Right, tab))) _pendingSplit = null;
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
        // Ao sair do ecrã dividido para outra aba, as metades seguram-se por cima até a nova apresentar (PageSwap, como
        // numa troca normal) — mas cada uma no seu lugar: postas logo na área inteira, a da esquerda via-se em ecrã
        // inteiro durante 4–5 fotogramas antes da nova. As faixas da divisão ficam até elas saírem.
        var all = _browsers.Select(p => (Tab: p.Key, Page: (FrameworkElement)p.Value))
            .Concat(_videoViews.Select(p => (Tab: p.Key, Page: (FrameworkElement)p.Value))).ToList();
        var leavingSplit = _split is null
            ? all.Where(p => p.Page.Visibility == Visibility.Visible && InSplitPane(p.Page) && !ShouldShow(p.Tab, p.Page))
                .Select(p => p.Page).ToHashSet()
            : [];
        if (_split is null)
        {
            if (leavingSplit.Count == 0)
            {
                columns.Clear();
                rows.Clear();
            }
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

        var pages = new List<(FrameworkElement, bool)>();
        foreach (var (tab, browser) in all)
        {
            var pane = _split?.PaneOf(tab);
            pages.Add((browser, ShouldShow(tab, browser)));
            if (!leavingSplit.Contains(browser)) PlaceInHost(browser, pane);
        }
        // Abas fechadas que estavam à vista: saem como uma aba que se esconde (por cima, até a vizinha aparecer).
        pages.AddRange(_retiringPages.Select(page => ((FrameworkElement)page, false)));
        ShowPages(pages);

        // Depois de a camada de sobreposição existir, a barra de carregamento vive lá (linha da página, ver
        // SetUpOverlayLayer): pô-la na linha 0 da grelha da camada levava-a para o topo da barra de título.
        if (ReferenceEquals(AuroraBar.Parent, BrowserHost)) PlaceInHost(AuroraBar, null);
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

        // O contorno só se pode medir depois de a grelha ter sido refeita.
        Dispatcher.InvokeAsync(UpdateFocusRing, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Contorno íris à volta da metade ativa, desenhado na camada de sobreposição (a página é uma janela própria e
    // tapava-o quando vivia na grelha). Medido a partir do elemento da metade, em coordenadas da moldura da janela.
    private void UpdateFocusRing()
    {
        if (_split?.PaneOf(Tabs.Active) is null || PaneElement(Tabs.Active!) is not { IsVisible: true } pane
            || pane.ActualWidth < 1 || pane.ActualHeight < 1 || !pane.IsDescendantOf(RootBorder))
        {
            SplitFocusRing.Visibility = Visibility.Collapsed;
            return;
        }
        var origin = pane.TranslatePoint(new Point(0, 0), RootBorder);
        // Ajuste e5: na janela principal, 2 px para fora da metade (a página tapa o resto); sem ele, na camada, por cima.
        var ring = SplitRingBounds(origin, new Size(pane.ActualWidth, pane.ActualHeight), RingOutsideOverlay);
        SplitFocusRing.Margin = new Thickness(ring.X, ring.Y, 0, 0);
        SplitFocusRing.Width = ring.Width;
        SplitFocusRing.Height = ring.Height;
        SplitFocusRing.Visibility = Visibility.Visible;
    }

    private FrameworkElement? PaneElement(BrowserTab tab)
        => _browsers.TryGetValue(tab, out var browser) ? browser : _videoViews.TryGetValue(tab, out var view) ? view : null;

    // Faixa à direita ou em baixo que acende enquanto uma aba arrastada está sobre a margem.
    private void ShowSplitDropHint(SplitDropTarget target)
    {
        if (target == SplitDropTarget.None)
        {
            SplitDropHint.Visibility = Visibility.Collapsed;
            return;
        }
        var padding = PageFrame.Padding;
        var right = target == SplitDropTarget.Right;
        SplitDropHint.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        SplitDropHint.VerticalAlignment = right ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        SplitDropHint.Width = right ? SplitDropZone.Width : double.NaN;
        SplitDropHint.Height = right ? double.NaN : SplitDropZone.Width;
        SplitDropHint.Margin = new Thickness(right ? 0 : PageFrame.Margin.Left + padding.Left, 0, PageFrame.Margin.Right + padding.Right, padding.Bottom);
        SplitDropHint.Visibility = Visibility.Visible;
    }

    // Posição de um elemento na grelha da página: uma metade (coluna ou linha conforme a orientação) ou a área toda.
    // Posta numa das metades (e não na área inteira).
    private static bool InSplitPane(UIElement element) => Grid.GetColumnSpan(element) < 3 || Grid.GetRowSpan(element) < 3;

    // A página da aba fica à vista: numa das metades, ou a da aba ativa fora do ecrã dividido.
    private bool ShouldShow(BrowserTab tab, FrameworkElement page)
    {
        // A meio da troca para o motor de vídeo, só a página do motor principal se vê (até o WebView2 pintar).
        if (page is Microsoft.Web.WebView2.Wpf.WebView2 && _videoSwitching.Contains(tab)) return false;
        return _split?.PaneOf(tab) is not null || (_split is null && ReferenceEquals(tab, Tabs.Active));
    }

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

    // Clicar numa das metades torna-a ativa; quando a metade muda de tamanho (divisor, janela), o contorno acompanha.
    private void WatchPaneFocus(BrowserTab tab, FrameworkElement browser)
    {
        browser.PreviewMouseDown += (_, _) =>
        {
            if (IsSplit && !ReferenceEquals(Tabs.Active, tab) && _split!.PaneOf(tab) is not null) Tabs.Active = tab;
        };
        browser.SizeChanged += (_, _) => { if (IsSplit && ReferenceEquals(tab, Tabs.Active)) UpdateFocusRing(); };
    }
}
