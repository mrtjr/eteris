using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Só liga os controlos à lógica em Tabs/TabRadar.cs (filtros, duplicadas, ordenações, estado).
public partial class TabRadarView : UserControl
{
    private readonly TabsViewModel _tabs;
    private readonly Action<BrowserTab> _toggleMute;
    private readonly Action<BrowserTab> _closeTab;
    private readonly Action<BrowserTab> _openSplit;
    private readonly Action _dismiss;
    private readonly Func<IReadOnlyList<Data.DoneTab>> _doneToday;
    private readonly bool _privateWindow;
    private readonly HashSet<BrowserTab> _watched = [];
    private bool _watching;
    private DispatcherOperation? _pendingRefresh;

    internal TabRadarView(TabsViewModel tabs, bool privateWindow, Action<BrowserTab> toggleMute,
        Action<BrowserTab> closeTab, Action<BrowserTab> openSplit, Action dismiss, Func<IReadOnlyList<Data.DoneTab>> doneToday)
    {
        _tabs = tabs;
        _privateWindow = privateWindow;
        _doneToday = doneToday;
        _toggleMute = toggleMute;
        _closeTab = closeTab;
        _openSplit = openSplit;
        _dismiss = dismiss;
        InitializeComponent();

        Filters.ItemsSource = privateWindow
            ? new[] { "Todas as abas", "Fixadas", "Com som", "A dormir", "Duplicadas", "Fechadas recentemente" }
            : new[] { "Todas as abas", "Fixadas", "Com som", "A dormir", "Duplicadas", "Fechadas recentemente", "Feitas hoje" };
        Order.ItemsSource = new[] { "Usadas recentemente", "Por site", "Ordem da faixa de abas" };
        Filters.SelectedIndex = (int)RadarScope.All;
        Order.SelectedIndex = (int)RadarSort.Recent;
        Filters.SelectionChanged += (_, _) => Refresh();
        Order.SelectionChanged += (_, _) => Refresh();
        QueryBox.TextChanged += (_, _) => Refresh();
        Results.SelectionChanged += (_, _) => ShowDetails();
        QueryBox.PreviewKeyDown += Query_PreviewKeyDown;
        Results.PreviewKeyDown += Results_PreviewKeyDown;
        Results.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(Results, source) is ListBoxItem)
                OpenSelected();
        };
        OpenButton.Click += (_, _) => OpenSelected();
        SplitButton.Click += (_, _) => WithOpenTab(tab =>
        {
            if (ReferenceEquals(tab, _tabs.Active)) return;
            _openSplit(tab);
            _dismiss();
        });
        PinButton.Click += (_, _) => WithOpenTab(_tabs.TogglePin);
        MuteButton.Click += (_, _) => WithOpenTab(_toggleMute);
        CloseTabButton.Click += (_, _) => WithOpenTab(tab =>
        {
            if (_tabs.Tabs.Count > 1) _closeTab(tab);
        });
        DismissButton.Click += (_, _) => _dismiss();
    }

    internal TextBox SearchInput => QueryBox;

    internal bool ClosePicker()
    {
        if (Filters.IsDropDownOpen)
        {
            Filters.IsDropDownOpen = false;
            return true;
        }

        if (!Order.IsDropDownOpen) return false;
        Order.IsDropDownOpen = false;
        return true;
    }

    internal void Start()
    {
        QueryBox.Clear();
        Filters.SelectedIndex = (int)RadarScope.All;
        Order.SelectedIndex = (int)RadarSort.Recent;
        _tabs.Tabs.CollectionChanged += CollectionChanged;
        _tabs.PropertyChanged += ActiveChanged;
        _watching = true;
        SyncSubscriptions();
        Refresh();
    }

    internal void Stop()
    {
        if (!_watching) return;
        _watching = false;
        _pendingRefresh?.Abort();
        _pendingRefresh = null;
        _tabs.Tabs.CollectionChanged -= CollectionChanged;
        _tabs.PropertyChanged -= ActiveChanged;
        foreach (var tab in _watched) tab.PropertyChanged -= TabChanged;
        _watched.Clear();
        Results.ItemsSource = null;
        QueryBox.Clear();
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncSubscriptions();
        QueueRefresh();
    }

    private void ActiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabsViewModel.Active)) QueueRefresh();
    }

    private void TabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTab.Title) or nameof(BrowserTab.Address) or nameof(BrowserTab.Favicon)
            or nameof(BrowserTab.Thumbnail) or nameof(BrowserTab.IsPinned) or nameof(BrowserTab.IsAudible)
            or nameof(BrowserTab.IsMuted) or nameof(BrowserTab.IsSleeping) or nameof(BrowserTab.Group) or nameof(BrowserTab.IsLocked)
            or nameof(BrowserTab.CustomTitle) or nameof(BrowserTab.Emoji) or nameof(BrowserTab.ColorIndex) or nameof(BrowserTab.Note))
            QueueRefresh();
    }

    private void SyncSubscriptions()
    {
        var current = _tabs.Tabs.ToHashSet();
        foreach (var tab in _watched.Except(current).ToArray())
        {
            tab.PropertyChanged -= TabChanged;
            _watched.Remove(tab);
        }

        foreach (var tab in current)
            if (_watched.Add(tab)) tab.PropertyChanged += TabChanged;
    }

    private void QueueRefresh()
    {
        if (!_watching || _pendingRefresh is not null) return;
        _pendingRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pendingRefresh = null;
            Refresh();
        }));
    }

    private void Refresh()
    {
        if (!_watching) return;

        var selection = Results.SelectedItem as RadarRow;
        var previousIndex = Math.Max(0, Results.SelectedIndex);
        var scope = (RadarScope)Filters.SelectedIndex;
        var sort = (RadarSort)Order.SelectedIndex;
        var query = QueryBox.Text.Trim();

        var visible = TabRadar.Rows(_tabs.Tabs, _tabs.Active, _tabs.RecentlyClosed, scope, sort, query,
            scope == RadarScope.Done ? _doneToday() : null);
        var retained = selection is null ? null : visible.FirstOrDefault(row => ReferenceEquals(row.Identity, selection.Identity));
        Results.ItemsSource = visible;
        Results.SelectedItem = retained ?? (visible.Count == 0 ? null : visible[Math.Min(previousIndex, visible.Count - 1)]);
        if (Results.SelectedItem is { } selected && !ReferenceEquals(selected, retained))
            Results.ScrollIntoView(selected);

        Summary.Text = TabRadar.Status(visible.Count, scope, query);
        ShowDetails();
    }

    private void ShowDetails()
    {
        var row = Results.SelectedItem as RadarRow;
        var tab = row?.Tab;
        DetailsTitle.Text = row?.Title ?? "Encontre uma aba";
        DetailsAddress.Text = row?.Address ?? string.Empty;
        DetailsState.Text = row is null ? "Use a pesquisa ou mude o filtro para começar."
            : row.Status.Length > 0 ? row.Status : "Aba aberta";
        DetailsNote.Text = row?.Note is { } note ? "✎ " + note : string.Empty;
        DetailsNote.Visibility = row?.Note is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewImage.Source = _privateWindow ? null : tab?.Thumbnail;
        PreviewImage.Visibility = PreviewImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewPlaceholder.Visibility = PreviewImage.Source is null ? Visibility.Visible : Visibility.Collapsed;

        OpenButton.Content = row?.Closed is not null || row?.Done is not null ? "Reabrir aba" : "Ir para a aba";
        OpenButton.IsEnabled = row is not null;
        SplitButton.IsEnabled = tab is not null && _tabs.Active is not null && !ReferenceEquals(tab, _tabs.Active);
        PinButton.Content = tab?.IsPinned == true ? "Desafixar" : "Fixar";
        PinButton.IsEnabled = tab is not null;
        MuteButton.Content = tab?.IsMuted == true ? "Ativar som" : "Silenciar";
        MuteButton.IsEnabled = tab is not null;
        CloseTabButton.IsEnabled = tab is not null && _tabs.Tabs.Count > 1;
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key is Key.Down or Key.Up) && Results.Items.Count > 0)
        {
            var index = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1);
            Results.SelectedIndex = index;
            Results.ScrollIntoView(Results.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected();
        e.Handled = true;
    }

    private void OpenSelected()
    {
        if (Results.SelectedItem is not RadarRow row) return;
        if (row.Tab is { } tab && _tabs.Tabs.Contains(tab))
            _tabs.Active = tab;
        else if (row.Done is { } done)
            _tabs.NewTab(done.Url);
        else if (row.Closed is not { } closed || _tabs.ReopenClosed(closed) is null)
            return;
        _dismiss();
    }

    private void WithOpenTab(Action<BrowserTab> action)
    {
        if (Results.SelectedItem is RadarRow { Tab: { } tab } && _tabs.Tabs.Contains(tab))
            action(tab);
    }
}
