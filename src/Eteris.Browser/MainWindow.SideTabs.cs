using System.Windows;
using System.Windows.Shell;
using Eteris.Browser.Data;
using Eteris.Browser.Theme;

namespace Eteris.Browser;

// Abas verticais: a faixa do topo dá lugar a uma barra lateral (encolhível a ícones);
// a barra de ferramentas passa a ser a zona de arrastar a janela e recebe os botões da janela.
public partial class MainWindow
{
    public const double SideWidth = 248;
    public const double SideCollapsedWidth = 56;

    public static readonly DependencyProperty SideCollapsedProperty = DependencyProperty.Register(
        nameof(SideCollapsed), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool SideCollapsed
    {
        get => (bool)GetValue(SideCollapsedProperty);
        set => SetValue(SideCollapsedProperty, value);
    }

    // Modo compacto em vigor (as abas vivem na barra de ferramentas): lido pelo estilo das abas.
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private bool VerticalTabs => Settings.Current.VerticalTabs;
    private bool CompactDensity => Settings.Current.Density == "compact";

    private ChromeLayout _chrome = ChromeLayout.Resolve(false, false, false);

    // Único sítio que aplica a disposição das barras; a decisão está em Theme/ChromeLayout.cs (abas no topo,
    // verticais, modo compacto, modo foco e ecrã inteiro).
    private void ApplyChromeLayout()
    {
        var layout = _chrome = ChromeLayout.Resolve(VerticalTabs, CompactDensity, _focusMode || _pageFullscreen);
        var hidden = layout.Mode == ChromeMode.Hidden;
        var side = layout.ShowsSideBar;
        SideCollapsed = Settings.Current.SidebarCollapsed;
        Compact = layout.TabsInToolbar;
        PlaceTabStrip(layout.TabsInToolbar);

        TitleRow.Height = new GridLength(layout.TitleHeight);
        ToolbarRow.Height = new GridLength(layout.ToolbarHeight);
        TitleBar.Visibility = layout.ShowsTitleBar ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = layout.ShowsToolbar ? Visibility.Visible : Visibility.Collapsed;
        ToolbarCaption.Visibility = layout.CaptionInToolbar ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompactOmnibox();
        UpdateOmniboxChrome();

        SideBar.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        SideFilterHost.Visibility = SideCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (SideCollapsed && SideFilter.Text.Length > 0) SideFilter.Clear();
        SideBar.Width = SideCollapsed ? SideCollapsedWidth : SideWidth;
        SideCollapseButton.Content = SideCollapsed ? "" : "";
        SideCollapseButton.ToolTip = SideCollapsed ? "Expandir a barra de abas" : "Encolher a barra de abas";
        PageFrame.Margin = new Thickness(side ? SideBar.Width - 6 : 0, 0, 0, 0);
        PageFrame.Padding = hidden ? new Thickness(0) : new Thickness(6, 0, 6, 6);

        WindowChrome.GetWindowChrome(this).CaptionHeight = layout.CaptionHeight;
        _pageRadius = layout.PageRadius;
        BrowserHost.Clip = new System.Windows.Media.RectangleGeometry(new Rect(BrowserHost.RenderSize), _pageRadius, _pageRadius);
    }

    private void SideCollapse_Click(object sender, RoutedEventArgs e)
        => Settings.Set("sidebarCollapsed", (!Settings.Current.SidebarCollapsed).ToString().ToLowerInvariant());

    private void ToggleVerticalTabs()
        => Settings.Set("verticalTabs", (!Settings.Current.VerticalTabs).ToString().ToLowerInvariant());

    private void ToggleCompactDensity()
        => Settings.Set("density", CompactDensity ? "normal" : "compact");

    // Modo compacto: a faixa de abas inteira (logótipo, abas, setas, «+», «▾») muda-se da barra de título para a
    // coluna própria da barra de ferramentas, e o endereço passa a ter largura fixa. Os mesmos controlos, sem
    // duplicar nada; os nomes continuam a funcionar no código.
    private void PlaceTabStrip(bool inToolbar)
    {
        var target = inToolbar ? (System.Windows.Controls.Panel)CompactTabHost : TitleBar;
        if (!ReferenceEquals(TabStripHost.Parent, target))
        {
            ((System.Windows.Controls.Panel)TabStripHost.Parent).Children.Remove(TabStripHost);
            target.Children.Add(TabStripHost);
        }
        TabStripPanel.VerticalAlignment = inToolbar ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        // Numa só linha cada pixel conta: o logótipo sai (a janela já se reconhece pela faixa íris da barra de endereço).
        BrandMark.Visibility = inToolbar ? Visibility.Collapsed : Visibility.Visible;
        ToolbarTabsColumn.Width = inToolbar ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ToolbarOmniboxColumn.Width = inToolbar ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }

    // Em modo compacto a barra de endereço tem largura fixa e cresce ao receber o foco (há mais para ler ao escrever).
    private void UpdateCompactOmnibox()
    {
        var width = ChromeLayout.OmniboxWidth(_chrome.Mode, Omnibox.IsKeyboardFocusWithin);
        if (width is null)
        {
            OmniboxFrame.BeginAnimation(WidthProperty, null);
            OmniboxFrame.Width = double.NaN;
            return;
        }
        var from = double.IsNaN(OmniboxFrame.Width) ? OmniboxFrame.ActualWidth : OmniboxFrame.Width;
        if (from <= 0) from = width.Value;
        OmniboxFrame.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(from, width.Value, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
    }

    private void WatchTabLayoutSetting()
    {
        void OnChanged(UserSettings _) => Dispatcher.InvokeAsync(() =>
        {
            ApplyChromeLayout();
            UpdateTabWidth();
        });
        Settings.Changed += OnChanged;
        Closed += (_, _) => Settings.Changed -= OnChanged;
        ApplyChromeLayout();
    }

    // ---------- Filtrar abas (barra lateral) ----------
    // Uma vista própria da lista: o filtro não mexe na faixa de abas do topo.
    private System.Windows.Data.ListCollectionView? _sideView;

    private void SetUpSideFilter()
    {
        _sideView = new System.Windows.Data.ListCollectionView(Tabs.Tabs);
        SideTabList.ItemsSource = _sideView;
    }

    private void SideFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_sideView is null) return;
        var query = SideFilter.Text.Trim();
        SideFilterHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _sideView.Filter = query.Length == 0 ? null : item => item is Eteris.Browser.Tabs.BrowserTab tab && Eteris.Browser.Tabs.TabFilter.Matches(tab.Title, tab.Address, query);
    }

    private void SideFilter_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape:
                SideFilter.Clear();
                ActiveBrowser?.Focus();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Enter when _sideView?.Cast<Eteris.Browser.Tabs.BrowserTab>().FirstOrDefault() is { } first:
                Tabs.Active = first;
                SideFilter.Clear();
                ActiveBrowser?.Focus();
                e.Handled = true;
                break;
        }
    }
}
