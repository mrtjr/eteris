using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow : Window
{
    // Logótipo (40) + botão de nova aba (36) + lista «▾» (28) + duas setas de rolagem (2×24) + folga (8).
    private const double TabStripReserved = 40 + 36 + 28 + 2 * TabLayout.ArrowWidth + 8;

    public static readonly DependencyProperty TabWidthProperty = DependencyProperty.Register(
        nameof(TabWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(TabLayout.MaxTabWidth));

    private readonly Dictionary<BrowserTab, ChromiumWebBrowser> _browsers = new();
    private readonly Dictionary<BrowserTab, ScriptletInjector> _scriptlets = new();

    public MainWindow(string? initialUrl = null, bool isPrivate = false)
    {
        InitializeComponent();
        IsPrivate = isPrivate;
        if (isPrivate) ConfigurePrivateWindow();
        DataContext = Tabs;
        Tabs.Tabs.CollectionChanged += OnTabsChanged;
        Tabs.PropertyChanged += OnTabsPropertyChanged;
        StateChanged += (_, _) => OnWindowStateChanged();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.Apply(this, BorderColor, darkMode: !UsesLight);
            ApplyBackdrop();
        };
        Deactivated += (_, _) => CloseSuggestions();
        LocationChanged += (_, _) => CloseSuggestions();
        Closed += (_, _) => DisposeBrowsers();
        AuroraBar.IsVisibleChanged += (_, _) => AnimateAurora(AuroraBar.IsVisible);
        StartTabs(initialUrl);
        RestorePlacement();
        WatchUpdates();
        WatchBackdropSetting();
        WatchTabLayoutSetting();
        WatchTheme();
        WatchMouseAndDrops();
        TrackActivation();
        RememberWhenClosing();
        StartAudioWatch();
        WatchMiniBar();
        WatchOmniboxMenu();
        SetUpSideFilter();
        Closing += (_, _) => FlushNotes();
        ContentRendered += (_, _) => SetUpOverlayLayer();
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
        // Mover não cria nem destrói abas: o item vem em OldItems e NewItems e não se pode recriar o browser.
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        foreach (BrowserTab tab in e.NewItems ?? Array.Empty<BrowserTab>())
        {
            tab.PropertyChanged += OnTabPropertyChanged;
            if (!tab.IsSleeping) CreateBrowser(tab);
        }

        foreach (BrowserTab tab in e.OldItems ?? Array.Empty<BrowserTab>())
        {
            OnSplitTabRemoved(tab);
            tab.PropertyChanged -= OnTabPropertyChanged;
            ReleaseBrowser(tab);
        }

        UpdateTabWidth();
    }

    private void CreateBrowser(BrowserTab tab)
    {
        if (TryCreateVideoView(tab)) return;
        // Scripts do Éteris que correm antes dos da página: regras cosméticas do site (escudo ligado), modo escuro (se
        // pedido), deteção de vídeo só em H.264 e registo do último clique (para distinguir um link aberto pelo
        // utilizador de um anúncio pop-under). Registados pelo DevTools: não dependem do processo filho do CefSharp.
        var injector = new ScriptletInjector(AppData.IsShieldOn,
            url => VideoEngine.CanHost(url)
                ? SiteCosmeticScript(url)
                  + (DarkPages.IsCandidate(url) && WebHost(url) is { } darkHost && AppData.IsDark(darkHost) ? DarkPages.EarlyScript + "\n;\n" : "")
                  + VideoEngine.WatchScript + "\n;\n" + PopupPolicy.ActivationScript + "\n;\n" + LinkPreview.AltClickScript
                : null);
        UserActivation? lastActivation = null;
        _scriptlets[tab] = injector;
        var browser = new ChromiumWebBrowser(tab.Address)
        {
            // A página é desenhada pelo próprio Chromium numa janela filha (como no Chrome, Firefox e Brave), sem a
            // cópia de cada quadro para o WPF: ~4× menos processador. Ver docs/paridade.md.
            Visibility = Visibility.Hidden,
            // Com a página em janela própria, o teclado e o foco vão direto para o Chromium: os atalhos do Éteris e a
            // escolha da metade ativa no ecrã dividido chegam por estes dois tratadores.
            KeyboardHandler = new ShortcutKeyboardHandler(ShortcutFromPage),
            FocusHandler = new PageFocusHandler(() => Dispatcher.InvokeAsync(() => OnPageFocused(tab))),
            LifeSpanHandler = new PopupToTabHandler(request => Dispatcher.InvokeAsync(
                () => DecidePopup(tab, request, () => Volatile.Read(ref lastActivation)))),
            DisplayHandler = new FaviconHandler((url, page) => Dispatcher.InvokeAsync(() => SetFavicon(tab, url, page)),
                fullscreen => Dispatcher.InvokeAsync(() => OnPageFullscreen(tab, fullscreen))),
            DownloadHandler = DownloadHandler,
            FindHandler = NewFindHandler(),
            RequestContext = PrivateContext,
            PermissionHandler = PermissionHandler,
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
                    ShieldStats.CountBlocked(persist: !IsPrivate);
                    Dispatcher.InvokeAsync(() => tab.RecordBlocked(host));
                },
                onCrashed: () => Dispatcher.InvokeAsync(() => OnPageCrashed(tab))),
        };
        browser.MenuHandler = NewPageMenuHandler(tab, browser);
        DependencyPropertyDescriptor.FromProperty(ChromiumWebBrowser.AddressProperty, typeof(ChromiumWebBrowser)).AddValueChanged(browser, (_, _) =>
        {
            if (browser.Address is not string address) return;
            // Na página de erro a omnibox continua a mostrar o endereço que falhou.
            if (ErrorPage.IsErrorPage(address)) address = ErrorPage.FailedUrl(address) ?? address;
            var hostChanged = !SameHost(tab.Address, address);
            if (hostChanged)
            {
                tab.Favicon = null;
                tab.Tint = null;
            }
            // O CefSharp pode repetir o mesmo endereço depois do fim do carregamento: só uma página nova apaga a deteção do leitor.
            if (!string.Equals(tab.Address, address, StringComparison.Ordinal)) tab.IsReaderable = false;
            tab.Address = address;
            if (hostChanged || browser.ZoomLevel == 0) ApplySiteZoom(browser, address);
            // Site lembrado como «só H.264» (o Twitch, por exemplo): a aba passa para o motor de vídeo.
            if (IsVideoSite(address)) Dispatcher.InvokeAsync(() => SwitchToVideoEngine(tab, address));
        });
        browser.ConsoleMessage += (_, e) =>
        {
            // Chega na thread do CEF; as definições avisam a interface, por isso guarda-se na thread da janela.
            if (PopupPolicy.IsActivationReport(e.Message, out var clickedLink, out var interactive))
            {
                Volatile.Write(ref lastActivation, new UserActivation(clickedLink, DateTime.UtcNow, interactive));
                return;
            }
            if (VideoEngine.IsReport(e.Message))
            {
                Dispatcher.InvokeAsync(() => OnPatentedVideoReport(tab, browser.Address ?? tab.Address));
                return;
            }
            if (LinkPreview.IsReport(e.Message, out var glanceUrl))
            {
                // Alt+clique num link: só da página à vista (a ativa ou uma das metades do ecrã dividido).
                Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(tab, Tabs.Active) || _split?.PaneOf(tab) is not null) ShowLinkPreview(glanceUrl);
                });
                return;
            }
            if (ReaderScripts.ParseReport(e.Message) is not { } reader) return;
            Dispatcher.InvokeAsync(() =>
            {
                Data.Settings.Set("readerTheme", reader.Theme);
                Data.Settings.Set("readerSize", reader.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Data.Settings.Set("readerSerif", reader.Serif ? "true" : "false");
            });
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
            if (!IsPrivate) AppData.TryWrite(h => h.UpdateTitle(url, title));
        };
        browser.LoadingStateChanged += (_, e) => Dispatcher.InvokeAsync(() =>
        {
            tab.IsLoading = e.IsLoading;
            tab.CanGoBack = e.CanGoBack;
            tab.CanGoForward = e.CanGoForward;
        });
        browser.LoadError += (_, e) =>
        {
            if (!e.Frame.IsMain || ErrorPage.IsErrorPage(e.FailedUrl)) return;
            var code = (int)e.ErrorCode;
            var target = HttpsFirst.IsUpgradeFailure(code) && ShieldEngine.Https.FallbackFor(e.FailedUrl) is { } insecure
                ? insecure
                : ErrorPage.ShouldShow(code) ? ErrorPage.Url(code, e.FailedUrl) : null;
            if (target is not null) Dispatcher.InvokeAsync(() => browser.Load(target));
        };
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            var url = e.Url;
            Dispatcher.InvokeAsync(() =>
            {
                DetectSiteTint(tab, browser, url);
                DetectReaderable(tab, browser, url);
                ApplyHiddenElements(browser, url);
                CaptureThumbnail(tab, browser, url);
                _ = ApplyGenericCosmeticsAsync(browser, url);
            });
            if (e.HttpStatusCode is <= 0 or >= 400) return;
            if (!IsPrivate) Dispatcher.InvokeAsync(() =>
            {
                AppData.TryWrite(h => h.Record(url, tab.Title, DateTime.UtcNow));
                RememberFavicon(tab, url);
            });
        };
        browser.StatusMessage += (_, e) =>
        {
            var text = Eteris.Browser.Omnibox.LinkStatus.Format(e.Value);
            Dispatcher.InvokeAsync(() => ShowLinkStatus(tab, text));
        };
        browser.IsBrowserInitializedChanged += (_, _) =>
        {
            if (browser.IsBrowserInitialized && tab.IsMuted) browser.GetBrowserHost()?.SetAudioMuted(true);
            // Abas abertas em segundo plano: a janela do Chromium fica logo escondida (ver SetPageWindowVisible).
            if (browser.IsBrowserInitialized) Dispatcher.InvokeAsync(LayoutBrowsers);
        };
        _browsers[tab] = browser;
        BrowserHost.Children.Add(browser);
        WatchPaneFocus(tab, browser);
        if (IsSplit) LayoutBrowsers();
    }

    private static bool SameHost(string a, string b) => BrowserTab.SameHost(a, b);

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabsViewModel.Active)) return;
        OnActiveTabChanging();
        OnSplitActiveChanged();
        _omniboxEdited = false;
        CloseFind();
        CloseSuggestions();
        SyncOmnibox();
        SyncLoading();
        UpdateShieldButton();
        UpdateStar();
        UpdateZoomBadge();
        UpdateVideoEngineBadge();
        ApplyTint();
        RevealActiveTab();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTab.Address) or nameof(BrowserTab.Title) or nameof(BrowserTab.IsPinned)
            or nameof(BrowserTab.Group) or nameof(BrowserTab.IsHiddenByGroup)) ScheduleSessionSave();
        if (e.PropertyName is nameof(BrowserTab.IsPinned) or nameof(BrowserTab.IsHiddenByGroup) or nameof(BrowserTab.IsGroupHead)) UpdateTabWidth();
        if (!ReferenceEquals(sender, Tabs.Active)) return;
        if (e.PropertyName == nameof(BrowserTab.Address)) { SyncOmnibox(); UpdateShieldButton(); UpdateStar(); UpdateZoomBadge(); }
        if (e.PropertyName == nameof(BrowserTab.IsLoading)) SyncLoading();
        if (e.PropertyName == nameof(BrowserTab.BlockedCount)) UpdateShieldButton();
        if (e.PropertyName == nameof(BrowserTab.Tint)) ApplyTint();
        if (e.PropertyName == nameof(BrowserTab.UsesVideoEngine)) UpdateVideoEngineBadge();
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
        RevealBarsForKeyboard();
        Tabs.NewTab(NewTabUrl);
        _omniboxEdited = false;
        CloseFind();
        SetOmniboxText(string.Empty);
        Omnibox.Focus();
        UpdateOmniboxChrome();
    }

    private void CloseTab(BrowserTab? tab)
    {
        if (tab is not null && Tabs.Close(tab)) Close();
    }

    private const double PinnedTabWidth = 44;

    // Largura de cada aba e da janela de rolagem da faixa. As abas encolhem até à largura mínima legível; a partir
    // daí a faixa (TabScroll) fica com a largura que sobra e rola — nenhuma aba desaparece nem fica ilegível.
    private void UpdateTabWidth()
    {
        var pinned = Tabs.PinnedCount;
        var (groupsReserved, visible) = GroupLayout();
        var badge = PrivateBadge.Visibility == Visibility.Visible ? PrivateBadge.ActualWidth + PrivateBadge.Margin.Right : 0;
        var strip = Math.Max(0, TabStripHost.ActualWidth - TabStripReserved - badge + (Compact ? BrandMark.Width : 0));
        TabScroll.MaxWidth = strip;
        TabWidth = TabLayout.TabWidth(strip - pinned * PinnedTabWidth - groupsReserved, visible);
        UpdateTabArrows();
        RevealActiveTab();
    }

    private void OnWindowStateChanged()
    {
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        // Maximizada com moldura, o Windows estende a janela para lá do ecrã; sem moldura (ecrã inteiro) não.
        RootBorder.Margin = maximized && WindowStyle != WindowStyle.None
            ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4)
            : new Thickness(0);
        MaximizeButton.Content = MaximizeButton2.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = MaximizeButton2.ToolTip = maximized ? "Restaurar" : "Maximizar";
        CloseSuggestions();
    }

    private void DisposeBrowsers()
    {
        foreach (var injector in _scriptlets.Values) injector.Dispose();
        _scriptlets.Clear();
        foreach (var browser in _browsers.Values)
        {
            PageScript.Release(browser);
            browser.Dispose();
        }
        _browsers.Clear();
        DisposeVideoViews();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        e.Handled = true;

        if (RadarOpen)
        {
            e.Handled = HandleRadarKey(key, ctrl, shift, alt);
            return;
        }

        // Alt sozinho em modo foco: as barras aparecem por uns segundos (como a barra de menus do Firefox).
        if (key is Key.LeftAlt or Key.RightAlt && !ctrl && !shift && BarsAutoHide)
        {
            RevealBarsForKeyboard();
            e.Handled = false;
            return;
        }

        // O menu do Éteris é um Popup sem pai: as suas teclas chegam aqui primeiro, pelo MenuButton.
        if (BrowserMenuOpen)
        {
            e.Handled = HandleBrowserMenuKey(key, ctrl, shift, alt);
            return;
        }

        if (key == Key.Escape && ShortcutsOpen) CloseShortcuts();
        else if (key == Key.Escape && GlanceOpen) CloseGlance();
        else if (Shortcut(key, ctrl, shift, alt) is { } action) action();
        else if (key == Key.Escape && _pageFullscreen) ExitPageFullscreen();
        else if (key == Key.Escape && Tabs.Active?.IsLoading == true && !Omnibox.IsKeyboardFocusWithin) StopActive();
        else e.Handled = false;
    }

    // Os atalhos do Éteris, iguais venha a tecla da interface ou da página (que, desenhada numa janela própria, recebe
    // o teclado diretamente — ver ShortcutFromPage). Só decide; a ação corre na thread da janela.
    // Ctrl+Alt nunca é atalho: é assim que o Windows comunica o AltGr (AltGr+W, AltGr+Q… escrevem caracteres).
    private Action? Shortcut(Key key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && alt) return null;
        if (alt && !ctrl && !shift && key == Key.F) return ToggleBrowserMenu;
        if (key == Key.F1 || (ctrl && key is Key.OemQuestion or Key.Divide)) return ToggleShortcuts;
        if (ctrl && shift && key == Key.A) return ToggleRadar;
        if (ctrl && key == Key.K) return TogglePalette;
        if (ctrl && key == Key.J) return ToggleDownloads;
        if (ctrl && key == Key.D) return ToggleBookmark;
        if ((ctrl && key == Key.F) || key == Key.F3) return OpenFind;
        if (key == Key.F11) return ToggleFocusMode;
        if (key == Key.F9) return ToggleReader;
        if (key == Key.F12 || (ctrl && shift && key == Key.I)) return ShowDevTools;
        if (ctrl && key == Key.U) return ViewSource;
        if (ctrl && key == Key.H) return OpenHistory;
        if (ctrl && shift && key == Key.L) return ShowLinkHints;
        if (ctrl && shift && key == Key.E && !IsPrivate) return ToggleNotes;
        if (ctrl && shift && key == Key.M) return ToggleMiniWindow;
        if (ctrl && shift && key == Key.O) return OpenBookmarks;
        if (ctrl && shift && key == Key.S) return () => CapturePage(fullPage: true);
        if (ctrl && key == Key.OemComma) return OpenSettings;
        if (ctrl && key == Key.P) return () => ActiveBrowser?.Print();
        if (ctrl && key is Key.OemPlus or Key.Add) return () => Zoom(1);
        if (ctrl && key is Key.OemMinus or Key.Subtract) return () => Zoom(-1);
        if (ctrl && key is Key.D0 or Key.NumPad0) return () => Zoom(0);
        if (ctrl && shift && key == Key.N) return () => OpenWindow(isPrivate: true);
        if (ctrl && key == Key.N) return () => OpenWindow(isPrivate: false);
        if (ctrl && shift && key == Key.T) return () => Tabs.ReopenClosed();
        if (ctrl && key == Key.T) return OpenNewTab;
        if (ctrl && (key == Key.W || key == Key.F4)) return () => CloseTab(Tabs.Active);
        if (ctrl && key == Key.Tab) return shift ? Tabs.SelectPrevious : Tabs.SelectNext;
        if (ctrl && key == Key.PageDown) return Tabs.SelectNext;
        if (ctrl && key == Key.PageUp) return Tabs.SelectPrevious;
        if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F6) return FocusOmnibox;
        if ((ctrl && shift && key == Key.R) || (ctrl && key == Key.F5)) return () => ReloadActive(ignoreCache: true);
        if ((ctrl && key == Key.R) || key == Key.F5) return () => ReloadActive();
        if (alt && key == Key.Left) return GoBack;
        if (alt && key == Key.Right) return GoForward;
        if (ctrl && key >= Key.D1 && key <= Key.D9) return () => Tabs.SelectByNumber(key - Key.D0);
        if (ctrl && key >= Key.NumPad1 && key <= Key.NumPad9) return () => Tabs.SelectByNumber(key - Key.NumPad0);
        return null;
    }

    // Tecla premida com o foco na página (thread do CEF): os atalhos do Éteris são consumidos e corridos na janela;
    // o Esc chega à página na mesma, e também fecha a folha de atalhos, sai do ecrã inteiro ou para o carregamento.
    private bool ShortcutFromPage(int virtualKey, bool ctrl, bool shift, bool alt)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        if (Shortcut(key, ctrl, shift, alt) is { } action)
        {
            Dispatcher.InvokeAsync(action);
            return true;
        }
        if (key is Key.LeftAlt or Key.RightAlt && !ctrl && !shift && BarsAutoHide) Dispatcher.InvokeAsync(RevealBarsForKeyboard);
        if (key == Key.Escape)
            Dispatcher.InvokeAsync(() =>
            {
                if (RadarOpen) CloseRadar();
                else if (ShortcutsOpen) CloseShortcuts();
                else if (_pageFullscreen) ExitPageFullscreen();
                else if (Tabs.Active?.IsLoading == true) StopActive();
            });
        return false;
    }

    // A página recebeu o foco (clique, Tab): no ecrã dividido, a metade em que se clicou passa a ser a ativa.
    private void OnPageFocused(BrowserTab tab)
    {
        CloseSuggestions();
        if (IsSplit && !ReferenceEquals(Tabs.Active, tab) && _split!.PaneOf(tab) is not null) Tabs.Active = tab;
    }

    private void TabStripHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidth();

    private void BrowserHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => BrowserHost.Clip = new RectangleGeometry(new Rect(e.NewSize), _pageRadius, _pageRadius);

    private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            CloseTab(tab);
            e.Handled = true;
            return;
        }
        // As abas não recebem foco (Focusable=False, para não roubarem o teclado à página), e um ListBoxItem só se
        // seleciona com o clique se conseguir o foco: por isso a aba é ativada aqui, ao carregar no botão, como no Chrome.
        // O «x» e a etiqueta do grupo têm as suas próprias ações.
        if (e.ChangedButton == MouseButton.Left && !IsInsideTabControl(e.OriginalSource as DependencyObject, (DependencyObject)sender))
            Tabs.Active = tab;
    }

    private static bool IsInsideTabControl(DependencyObject? node, DependencyObject tabItem)
    {
        for (; node is not null && !ReferenceEquals(node, tabItem); node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase || node is FrameworkElement { Name: "GroupPill" }) return true;
        return false;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
        => CloseTab((sender as FrameworkElement)?.DataContext as BrowserTab);

    private void NewTab_Click(object sender, RoutedEventArgs e) => OpenNewTab();

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active?.IsLoading == true) StopActive();
        else ReloadActive();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}
