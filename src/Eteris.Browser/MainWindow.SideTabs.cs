using System.Windows;
using System.Windows.Shell;
using Eteris.Browser.Data;

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

    private bool VerticalTabs => Settings.Current.VerticalTabs;

    // Único sítio que decide a altura das barras: combina abas verticais e modo foco.
    private void ApplyChromeLayout()
    {
        var hidden = _focusMode || _pageFullscreen;
        var side = VerticalTabs && !hidden;
        SideCollapsed = Settings.Current.SidebarCollapsed;

        TitleRow.Height = new GridLength(hidden || VerticalTabs ? 0 : 42);
        ToolbarRow.Height = new GridLength(hidden ? 0 : 48);
        TitleBar.Visibility = hidden || VerticalTabs ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        ToolbarCaption.Visibility = side ? Visibility.Visible : Visibility.Collapsed;

        SideBar.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        SideBar.Width = SideCollapsed ? SideCollapsedWidth : SideWidth;
        SideCollapseButton.Content = SideCollapsed ? "" : "";
        SideCollapseButton.ToolTip = SideCollapsed ? "Expandir a barra de abas" : "Encolher a barra de abas";
        PageFrame.Margin = new Thickness(side ? SideBar.Width - 6 : 0, 0, 0, 0);
        PageFrame.Padding = hidden ? new Thickness(0) : new Thickness(6, 0, 6, 6);

        WindowChrome.GetWindowChrome(this).CaptionHeight = hidden ? 0 : VerticalTabs ? 48 : 42;
        _pageRadius = hidden ? 0 : 10;
        BrowserHost.Clip = new System.Windows.Media.RectangleGeometry(new Rect(BrowserHost.RenderSize), _pageRadius, _pageRadius);
    }

    private void SideCollapse_Click(object sender, RoutedEventArgs e)
        => Settings.Set("sidebarCollapsed", (!Settings.Current.SidebarCollapsed).ToString().ToLowerInvariant());

    private void ToggleVerticalTabs()
        => Settings.Set("verticalTabs", (!Settings.Current.VerticalTabs).ToString().ToLowerInvariant());

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
}
