using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CefSharp;
using CefSharp.Wpf;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow : Window
{
    private const double TabStripReserved = 40 + 36 + 56;

    public static readonly DependencyProperty TabWidthProperty = DependencyProperty.Register(
        nameof(TabWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(TabLayout.MaxTabWidth));

    private readonly Dictionary<BrowserTab, ChromiumWebBrowser> _browsers = new();
    private readonly Dictionary<BrowserTab, ScriptletInjector> _scriptlets = new();

    public MainWindow(string? initialUrl = null)
    {
        InitializeComponent();
        DataContext = Tabs;
        Tabs.Tabs.CollectionChanged += OnTabsChanged;
        Tabs.PropertyChanged += OnTabsPropertyChanged;
        StateChanged += (_, _) => OnWindowStateChanged();
        SourceInitialized += (_, _) => WindowEffects.Apply(this, borderColorRef: 0x0042302B);
        Deactivated += (_, _) => CloseSuggestions();
        LocationChanged += (_, _) => CloseSuggestions();
        Closed += (_, _) => DisposeBrowsers();
        AuroraBar.IsVisibleChanged += (_, _) => AnimateAurora(AuroraBar.IsVisible);
        StartTabs(initialUrl);
    }

    public TabsViewModel Tabs { get; } = new();

    public double TabWidth
    {
        get => (double)GetValue(TabWidthProperty);
        set => SetValue(TabWidthProperty, value);
    }

    private ChromiumWebBrowser? ActiveBrowser
        => Tabs.Active is { } tab && _browsers.TryGetValue(tab, out var browser) ? browser : null;

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (BrowserTab tab in e.NewItems ?? Array.Empty<BrowserTab>())
        {
            tab.PropertyChanged += OnTabPropertyChanged;
            CreateBrowser(tab);
        }

        foreach (BrowserTab tab in e.OldItems ?? Array.Empty<BrowserTab>())
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            if (_scriptlets.Remove(tab, out var injector)) injector.Dispose();
            if (_browsers.Remove(tab, out var browser))
            {
                BrowserHost.Children.Remove(browser);
                browser.Dispose();
            }
        }

        UpdateTabWidth();
    }

    private void CreateBrowser(BrowserTab tab)
    {
        var injector = new ScriptletInjector(AppData.IsShieldOn);
        _scriptlets[tab] = injector;
        var browser = new ChromiumWebBrowser(tab.Address)
        {
            Visibility = Visibility.Hidden,
            LifeSpanHandler = new PopupToTabHandler(
                (gesture, url, opener) => PopupPolicy.Allow(gesture, url, opener, ShieldEngine.Current, AppData.IsShieldOn),
                (url, foreground) => Dispatcher.InvokeAsync(() => Tabs.NewTab(url, foreground)),
                host =>
                {
                    ShieldStats.CountBlocked();
                    Dispatcher.InvokeAsync(() => tab.RecordBlocked(host));
                }),
            DisplayHandler = new FaviconHandler(url => Dispatcher.InvokeAsync(() => SetFavicon(tab, url))),
            RenderProcessMessageHandler = new CosmeticFilterHandler(AppData.IsShieldOn),
            DownloadHandler = DownloadHandler,
            RequestHandler = new PrivacyRequestHandler(
                () => ShieldEngine.Current,
                AppData.IsShieldOn,
                onNavigate: (cefBrowser, url) =>
                {
                    Dispatcher.InvokeAsync(tab.ResetShield);
                    return injector.Prepare(cefBrowser, url);
                },
                onBlocked: host =>
                {
                    ShieldStats.CountBlocked();
                    Dispatcher.InvokeAsync(() => tab.RecordBlocked(host));
                }),
        };
        browser.AddressChanged += (_, e) =>
        {
            if (e.NewValue is not string address) return;
            if (!SameHost(tab.Address, address))
            {
                tab.Favicon = null;
                tab.Tint = null;
            }
            tab.Address = address;
        };
        browser.TitleChanged += (_, e) =>
        {
            if (e.NewValue is not string { Length: > 0 } title)
            {
                tab.Title = tab.Address;
                return;
            }
            tab.Title = title;
            var url = tab.Address;
            AppData.TryWrite(h => h.UpdateTitle(url, title));
        };
        browser.LoadingStateChanged += (_, e) => Dispatcher.InvokeAsync(() =>
        {
            tab.IsLoading = e.IsLoading;
            tab.CanGoBack = e.CanGoBack;
            tab.CanGoForward = e.CanGoForward;
        });
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            var url = e.Url;
            Dispatcher.InvokeAsync(() =>
            {
                DetectSiteTint(tab, browser, url);
                _ = ApplyGenericCosmeticsAsync(browser, url);
            });
            if (e.HttpStatusCode is <= 0 or >= 400) return;
            Dispatcher.InvokeAsync(() => AppData.TryWrite(h => h.Record(url, tab.Title, DateTime.UtcNow)));
        };
        _browsers[tab] = browser;
        BrowserHost.Children.Add(browser);
    }

    private static bool SameHost(string a, string b)
        => Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub)
           && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase);

    private static void SetFavicon(BrowserTab tab, string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            tab.Favicon = null;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 32;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.DownloadFailed += (_, _) => tab.Favicon = null;
            image.DecodeFailed += (_, _) => tab.Favicon = null;
            tab.Favicon = image;
        }
        catch
        {
            tab.Favicon = null;
        }
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabsViewModel.Active)) return;
        foreach (var (tab, browser) in _browsers)
            browser.Visibility = ReferenceEquals(tab, Tabs.Active) ? Visibility.Visible : Visibility.Hidden;
        _omniboxEdited = false;
        CloseSuggestions();
        SyncOmnibox();
        SyncLoading();
        UpdateShieldButton();
        UpdateStar();
        ApplyTint();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Address)) ScheduleSessionSave();
        if (!ReferenceEquals(sender, Tabs.Active)) return;
        if (e.PropertyName == nameof(BrowserTab.Address)) { SyncOmnibox(); UpdateShieldButton(); UpdateStar(); }
        if (e.PropertyName == nameof(BrowserTab.IsLoading)) SyncLoading();
        if (e.PropertyName == nameof(BrowserTab.BlockedCount)) UpdateShieldButton();
        if (e.PropertyName == nameof(BrowserTab.Tint)) ApplyTint();
    }

    private void SyncLoading()
        => AuroraBar.Visibility = Tabs.Active?.IsLoading == true ? Visibility.Visible : Visibility.Collapsed;

    private void AnimateAurora(bool run)
    {
        if (AuroraBar.Fill is not LinearGradientBrush { RelativeTransform: TranslateTransform shift }) return;
        shift.BeginAnimation(TranslateTransform.XProperty, run
            ? new DoubleAnimation(0, 0.5, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void OpenNewTab()
    {
        Tabs.NewTab(NewTabPage.Url);
        _omniboxEdited = false;
        SetOmniboxText(string.Empty);
        Omnibox.Focus();
        UpdateOmniboxChrome();
    }

    private void CloseTab(BrowserTab? tab)
    {
        if (tab is not null && Tabs.Close(tab)) Close();
    }

    private void UpdateTabWidth()
        => TabWidth = TabLayout.TabWidth(TabStripHost.ActualWidth - TabStripReserved, Tabs.Tabs.Count);

    private void OnWindowStateChanged()
    {
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Margin = maximized ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
        CloseSuggestions();
    }

    private void DisposeBrowsers()
    {
        foreach (var injector in _scriptlets.Values) injector.Dispose();
        _scriptlets.Clear();
        foreach (var browser in _browsers.Values) browser.Dispose();
        _browsers.Clear();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        e.Handled = true;

        if (ctrl && key == Key.K) TogglePalette();
        else if (ctrl && key == Key.J) ToggleDownloads();
        else if (ctrl && key == Key.D) ToggleBookmark();
        else if (key == Key.F11) ToggleFocusMode();
        else if (ctrl && key is Key.OemPlus or Key.Add) Zoom(1);
        else if (ctrl && key is Key.OemMinus or Key.Subtract) Zoom(-1);
        else if (ctrl && key is Key.D0 or Key.NumPad0) Zoom(0);
        else if (ctrl && shift && key == Key.T) Tabs.ReopenClosed();
        else if (ctrl && key == Key.T) OpenNewTab();
        else if (ctrl && (key == Key.W || key == Key.F4)) CloseTab(Tabs.Active);
        else if (ctrl && key == Key.Tab) { if (shift) Tabs.SelectPrevious(); else Tabs.SelectNext(); }
        else if (ctrl && key == Key.PageDown) Tabs.SelectNext();
        else if (ctrl && key == Key.PageUp) Tabs.SelectPrevious();
        else if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F6) FocusOmnibox();
        else if ((ctrl && shift && key == Key.R) || (ctrl && key == Key.F5)) ActiveBrowser?.Reload(ignoreCache: true);
        else if ((ctrl && key == Key.R) || key == Key.F5) ActiveBrowser?.Reload();
        else if (alt && key == Key.Left) ActiveBrowser?.Back();
        else if (alt && key == Key.Right) ActiveBrowser?.Forward();
        else if (ctrl && key >= Key.D1 && key <= Key.D9) Tabs.SelectByNumber(key - Key.D0);
        else if (ctrl && key >= Key.NumPad1 && key <= Key.NumPad9) Tabs.SelectByNumber(key - Key.NumPad0);
        else if (key == Key.Escape && Tabs.Active?.IsLoading == true && !Omnibox.IsKeyboardFocusWithin) ActiveBrowser?.Stop();
        else e.Handled = false;
    }

    private void TabStripHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidth();

    private void BrowserHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => BrowserHost.Clip = new RectangleGeometry(new Rect(e.NewSize), _pageRadius, _pageRadius);

    private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        CloseTab((sender as FrameworkElement)?.DataContext as BrowserTab);
        e.Handled = true;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
        => CloseTab((sender as FrameworkElement)?.DataContext as BrowserTab);

    private void NewTab_Click(object sender, RoutedEventArgs e) => OpenNewTab();

    private void Back_Click(object sender, RoutedEventArgs e) => ActiveBrowser?.Back();

    private void Forward_Click(object sender, RoutedEventArgs e) => ActiveBrowser?.Forward();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active?.IsLoading == true) ActiveBrowser?.Stop();
        else ActiveBrowser?.Reload();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}
