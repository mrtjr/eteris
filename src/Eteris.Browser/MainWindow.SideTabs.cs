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

    // Alturas das linhas das barras como propriedades animáveis (um GridLength não se anima): as barras deslizam ao
    // aparecer e ao esconder-se em vez de saltar. A página, em janela própria, acompanha cada quadro.
    public static readonly DependencyProperty TitleRowHeightProperty = DependencyProperty.Register(
        nameof(TitleRowHeight), typeof(double), typeof(MainWindow),
        new PropertyMetadata(ChromeLayout.TitleBarHeight, (d, e) =>
        {
            var window = (MainWindow)d;
            window.TitleRow.Height = new GridLength((double)e.NewValue);
            // A barra de carregamento acompanha as alturas pelo próprio callback (sem vigilância no descritor: MOT-14).
            window.PlaceAurora();
        }));

    public static readonly DependencyProperty ToolbarRowHeightProperty = DependencyProperty.Register(
        nameof(ToolbarRowHeight), typeof(double), typeof(MainWindow),
        new PropertyMetadata(ChromeLayout.ToolbarBarHeight, (d, e) =>
        {
            var window = (MainWindow)d;
            window.ToolbarRow.Height = new GridLength((double)e.NewValue);
            window.PlaceAurora();
        }));

    public double TitleRowHeight
    {
        get => (double)GetValue(TitleRowHeightProperty);
        set => SetValue(TitleRowHeightProperty, value);
    }

    public double ToolbarRowHeight
    {
        get => (double)GetValue(ToolbarRowHeightProperty);
        set => SetValue(ToolbarRowHeightProperty, value);
    }

    private static readonly TimeSpan BarsSlide = TimeSpan.FromMilliseconds(170);

    // Uma barra que vai desaparecer fica visível até a altura chegar a zero; uma que vai aparecer fica visível já.
    private void SlideBar(DependencyProperty heightProperty, double current, double target, FrameworkElement bar, bool animate)
    {
        if (!animate || !IsLoaded || Math.Abs(current - target) < 0.5)
        {
            BeginAnimation(heightProperty, null);
            SetValue(heightProperty, target);
            bar.Visibility = target > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        bar.Visibility = Visibility.Visible;
        var slide = new System.Windows.Media.Animation.DoubleAnimation(current, target, BarsSlide)
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        if (target <= 0)
            slide.Completed += (_, _) =>
            {
                // Só se, entretanto, nada voltou a pedir a barra.
                if ((double)GetValue(heightProperty) <= 0.5) bar.Visibility = Visibility.Collapsed;
            };
        BeginAnimation(heightProperty, slide);
    }

    // Único sítio que aplica a disposição das barras; a decisão está em Theme/ChromeLayout.cs (abas no topo,
    // verticais, modo compacto, modo foco e ecrã inteiro).
    private void ApplyChromeLayout(bool animate = true)
    {
        SyncAutoHide();
        var layout = _chrome = ChromeLayout.Resolve(VerticalTabs, CompactDensity, _focusMode || _pageFullscreen, peek: _autoHide.Revealed);
        var hidden = layout.Mode == ChromeMode.Hidden;
        var side = layout.ShowsSideBar;
        SideCollapsed = Settings.Current.SidebarCollapsed;
        Compact = layout.TabsInToolbar;
        PlaceTabStrip(layout.TabsInToolbar);

        SlideBar(TitleRowHeightProperty, TitleRowHeight, layout.ShowsTitleBar ? layout.TitleHeight : 0, TitleBar, animate);
        SlideBar(ToolbarRowHeightProperty, ToolbarRowHeight, layout.ShowsToolbar ? layout.ToolbarHeight : 0, Toolbar, animate);
        ToolbarCaption.Visibility = layout.CaptionInToolbar ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompactOmnibox();
        UpdateOmniboxChrome();

        SideBar.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        SideFilterHost.Visibility = SideCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (SideCollapsed && SideFilter.Text.Length > 0) SideFilter.Clear();
        SideBar.Width = SideCollapsed ? SideCollapsedWidth : SideWidth;
        SideCollapseButton.Content = SideCollapsed ? "" : "";
        SideCollapseButton.ToolTip = SideCollapsed ? "Expandir a barra de abas" : "Encolher a barra de abas";
        // A margem da página (abas verticais e barra lateral) é posta por PlaceSidebar.
        PlaceSidebar();
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

    // Em modo compacto a barra de endereço tem largura própria e cresce ao receber o foco (há mais para ler ao
    // escrever), mas cede à faixa de abas em janelas estreitas (ver ChromeLayout.OmniboxWidth).
    private void UpdateCompactOmnibox(bool animate = true)
    {
        var width = ChromeLayout.OmniboxWidth(_chrome.Mode, Omnibox.IsKeyboardFocusWithin, CompactSharedWidth());
        if (width is null)
        {
            OmniboxFrame.BeginAnimation(WidthProperty, null);
            OmniboxFrame.Width = double.NaN;
            return;
        }
        var from = double.IsNaN(OmniboxFrame.Width) ? OmniboxFrame.ActualWidth : OmniboxFrame.Width;
        if (from <= 0 || !animate || Math.Abs(from - width.Value) < 1)
        {
            OmniboxFrame.BeginAnimation(WidthProperty, null);
            OmniboxFrame.Width = width.Value;
            return;
        }
        OmniboxFrame.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(from, width.Value, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
    }

    // Largura que o endereço e a faixa de abas repartem na linha única do modo compacto: a barra de ferramentas
    // sem a navegação, os botões da direita e os da janela.
    private double CompactSharedWidth()
    {
        if (CompactTabHost.Parent is not System.Windows.Controls.Grid row || row.ActualWidth <= 0) return double.PositiveInfinity;
        var others = 0.0;
        for (var i = 0; i < row.ColumnDefinitions.Count; i++)
            if (!ReferenceEquals(row.ColumnDefinitions[i], ToolbarTabsColumn) && !ReferenceEquals(row.ColumnDefinitions[i], ToolbarOmniboxColumn))
                others += row.ColumnDefinitions[i].ActualWidth;
        return row.ActualWidth - others - CompactTabHost.Margin.Left - OmniboxFrame.Margin.Left - OmniboxFrame.Margin.Right;
    }

    private void WatchTabLayoutSetting()
    {
        // Encostar a janela a meio ecrã ou mudar os botões da direita: o endereço compacto volta a repartir a linha.
        SizeChanged += (_, _) => { if (_chrome.Mode == ChromeMode.Compact) UpdateCompactOmnibox(animate: false); };
        ToolbarRight.SizeChanged += (_, _) => { if (_chrome.Mode == ChromeMode.Compact) UpdateCompactOmnibox(animate: false); };
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
        // A lista só lê a aba ativa (a ligação e a vista ficam em SideTabSelection): UIX-02.
        Eteris.Browser.Tabs.SideTabSelection.Attach(SideTabList, _sideView);
    }

    private void SideFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_sideView is null) return;
        var query = SideFilter.Text.Trim();
        SideFilterHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _sideView.Filter = query.Length == 0 ? null : item => item is Eteris.Browser.Tabs.BrowserTab tab && Eteris.Browser.Tabs.TabFilter.Matches(tab.DisplayTitle + " " + tab.Title + " " + tab.Note, tab.Address, query);
        Eteris.Browser.Tabs.SideTabSelection.Refresh(SideTabList);
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
