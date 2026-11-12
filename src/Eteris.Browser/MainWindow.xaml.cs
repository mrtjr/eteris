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

    public MainWindow(string? initialUrl = null, bool isPrivate = false, Data.SessionWindow? restored = null)
    {
        InitializeComponent();
        StartupTrace.Mark("janela.xaml");
        ApplyPerfDiagnostics();
        // O motor arranca numa thread própria durante o XAML (Infrastructure/CefConfigurator.cs): daqui em diante é preciso.
        CefConfigurator.WaitReady();
        if (StartupTrace.IsOn)
        {
            SourceInitialized += (_, _) => StartupTrace.MarkOnce("janela.sourceinit");
            Loaded += (_, _) => StartupTrace.MarkOnce("janela.loaded");
            ContentRendered += (_, _) => StartupTrace.MarkOnce("janela.render");
        }
        IsPrivate = isPrivate;
        if (isPrivate)
        {
            // Nada da janela privada fica no perfil do JIT do arranque (Infrastructure/StartupJit.cs).
            StartupJit.Stop();
            ConfigurePrivateWindow();
        }
        Tabs.NewTabAddress = NewTabUrl;
        DataContext = Tabs;
        Tabs.Tabs.CollectionChanged += OnTabsChanged;
        Tabs.PropertyChanged += OnTabsPropertyChanged;
        // Fechar (ou tirar da faixa) a metade ativa do ecrã dividido deixa a outra metade no ecrã, não a vizinha na faixa.
        Tabs.PreferredSuccessor = tab => _split?.PartnerOf(tab);
        StateChanged += (_, _) => OnWindowStateChanged();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.Apply(this, BorderColor, darkMode: !UsesLight);
            ApplyBackdrop();
        };
        Deactivated += (_, _) => CloseSuggestions();
        // Como os menus do Chrome, do Firefox e do Brave: a janela perde a ativação (outra janela, Alt+Tab), o menu fecha.
        Deactivated += (_, _) => CloseBrowserMenu();
        // Fechado assim, com o teclado no menu: ao voltar à janela, o teclado volta a quem o tinha (a página…).
        Activated += (_, _) => RestoreFocusAfterBrowserMenuOnActivate();
        // A barra de aprovação de um computador novo: só regista a candidata (nada se cria nem se lê aqui).
        InitPairingBar();
        LocationChanged += (_, _) => CloseSuggestions();
        Closed += (_, _) => DisposeBrowsers();
        Closed += (_, _) => ForgetVaultWindow();
        AuroraBar.IsVisibleChanged += (_, _) => AnimateAurora(AuroraBar.IsVisible && WindowState != WindowState.Minimized);
        StartTabs(initialUrl, restored);
        StartupTrace.Mark("janela.abas");
        WatchSnoozed();
        WatchArchive();
        RestorePlacement(restored?.Placement);
        WatchUpdates();
        WatchBackdropSetting();
        WatchTabLayoutSetting();
        WatchTheme();
        WatchMouseAndDrops();
        TrackActivation();
        RememberWhenClosing();
        WatchForgetOnClose();
        StartBounceWatch();
        StartAudioWatch();
        SetUpLiveDot();
        WatchUnseen();
        WatchMiniBar();
        WatchOmniboxMenu();
        SetUpSideFilter();
        WatchWorkspaces();
        WatchSidebar();
        WatchAltTap();
        Closing += (_, _) => FlushNotes();
        ContentRendered += (_, _) => SetUpOverlayLayer();
        ContentRendered += (_, _) => WireSignInBar();
        StartupTrace.Mark("janela.ctor");
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
        // Mover não cria nem destrói abas: o item vem em OldItems e NewItems e não se pode recriar o browser. A aba
        // ativa pode ter saltado para fora da janela de rolagem (adicionar ao grupo, desagrupar, ordenar por site):
        // volta a ser trazida à vista, menos durante o arrasto, que rola a faixa por si.
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            if (!_dragging) RevealActiveTab();
            return;
        }
        foreach (BrowserTab tab in e.NewItems ?? Array.Empty<BrowserTab>())
        {
            tab.PropertyChanged += OnTabPropertyChanged;
            // Uma aba que volta de outro Espaço acordada (trancada ou com som) ainda tem o motor: não se cria outro.
            if (!tab.IsSleeping && !HasEngine(tab)) CreateBrowser(tab);
        }

        foreach (BrowserTab tab in e.OldItems ?? Array.Empty<BrowserTab>())
        {
            OnSplitTabRemoved(tab);
            tab.PropertyChanged -= OnTabPropertyChanged;
            // Foi para a reserva de outro Espaço sem adormecer (trancada ou com som): o motor fica, escondido.
            if (!tab.IsSleeping && Tabs.WorkspaceOf(tab) is { IsCurrent: false }) continue;
            ReleaseBrowser(tab);
        }


        UpdateTabWidth();
    }

    private void CreateBrowser(BrowserTab tab)
    {
        if (TryCreateVideoView(tab)) return;
        StartupTrace.Mark("aba.criar", "abas=" + _browsers.Count);
        // Scripts do Éteris que correm antes dos da página. Por site: regras cosméticas do site (escudo ligado) e modo
        // escuro (se pedido). Base, em todas as páginas web: deteção de vídeo só em H.264 e registo do último clique
        // (para distinguir um link aberto pelo utilizador de um anúncio pop-under); também nas internas, o Alt+clique.
        // Registados pelo DevTools antes da navegação (ver ScriptletInjector): não dependem do processo filho do CefSharp.
        var injector = new ScriptletInjector(ShieldOn, SiteEarlyScript, TabBaseScript, hostKey: SiteEarlyKey);
        UserActivation? lastActivation = null;
        var mainHistory = new MainFrameHistory();
        var mainRequests = 0;
        var mainRequestsAtCommit = 0;
        string? committedUrl = null;
        _scriptlets[tab] = injector;
        var browser = new PageBrowser(tab.Address)
        {
            // A página é desenhada pelo próprio Chromium numa janela filha (como no Chrome, Firefox e Brave), sem a
            // cópia de cada quadro para o WPF: ~4× menos processador. Ver docs/paridade.md.
            Visibility = Visibility.Hidden,
            // A janela do CEF nasce já com o tamanho da área das páginas (uma aba nova não tem ainda tamanho no WPF).
            BirthSize = PageAreaPixels,
            // Com a página em janela própria, o teclado e o foco vão direto para o Chromium: os atalhos do Éteris e a
            // escolha da metade ativa no ecrã dividido chegam por estes dois tratadores.
            KeyboardHandler = new ShortcutKeyboardHandler((key, ctrl, shift, alt) => ShortcutFromPage(key, ctrl, shift, alt, tab), KeyUpFromPage),
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
                ShieldOn,
                onBlocked: host =>
                {
                    ShieldStats.CountBlocked(persist: !IsPrivate);
                    Dispatcher.InvokeAsync(() => tab.RecordBlocked(host));
                },
                onCrashed: () => Dispatcher.InvokeAsync(() => OnPageCrashed(tab)),
                // Página nova: a contagem do Escudo recomeça já no pedido (os bloqueios da página nova vêm depois dele).
                onMainFrameRequest: () =>
                {
                    Interlocked.Increment(ref mainRequests);
                    var leaving = Volatile.Read(ref committedUrl);
                    Dispatcher.InvokeAsync(() => tab.LeavePage(leaving));
                },
                isPrivate: IsPrivate,
                localNetworkAllowed: IsLocalNetworkAllowed,
                onLocalNetworkBlocked: (site, url) => OnLocalNetworkBlocked(tab, site, url),
                onTrackingSkipped: hosts => Dispatcher.InvokeAsync(() =>
                {
                    tab.RecordTrackingSkips(hosts);
                    if (ReferenceEquals(tab, Tabs.Active)) UpdateShieldButton();
                }),
                onMainNavigation: nav =>
                {
                    Volatile.Write(ref mainHistory.Last, nav);
                    OnMainNavigation(tab, nav);
                },
                onCloaked: (host, target) =>
                {
                    // Conta como rastreador bloqueado (na lista com o nome verdadeiro) e na linha própria do Escudo.
                    ShieldStats.CountBlocked(persist: !IsPrivate);
                    var label = CnameUncloak.Label(host, target);
                    Dispatcher.InvokeAsync(() =>
                    {
                        tab.RecordCloaked(label);
                        tab.RecordBlocked(label);
                    });
                },
                observatory: tab.Observatory),
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
            else tab.ForgetFaviconOfOtherSite(address);
            // O CefSharp pode repetir o mesmo endereço depois do fim do carregamento: só uma página nova apaga a deteção do leitor.
            if (!string.Equals(tab.Address, address, StringComparison.Ordinal))
            {
                tab.IsReaderable = false;
                tab.VideoOffer = null;
            }
            // «Esquecer ao fechar»: ir para outro site também é sair do anterior.
            if (hostChanged) NoteSiteLeft(tab.Address, address);
            tab.Address = address;
            if (hostChanged || browser.ZoomLevel == 0) ApplySiteZoom(browser, address);
            // Site lembrado como «só H.264» (o Twitch, por exemplo): a aba passa para o motor de vídeo.
            // Nunca a partir da página de erro ou do aviso do «HTTPS primeiro»: o endereço que lá aparece é o que falhou
            // (o http:// por aceitar), e o motor de vídeo abri-lo-ia sem a pessoa escolher «Continuar».
            // Nem a página a que se regressou com o Voltar do motor de vídeo (Tabs/EngineCrossing.cs).
            if (IsVideoSite(address) && !ErrorPage.IsErrorPage(browser.Address) && !tab.Crossing.StaysInMain(address))
                Dispatcher.InvokeAsync(() => SwitchToVideoEngine(tab, address));
            // Depois do commit (o DevTools volta a entregar mensagens): os scripts do site atual ficam registados, e os
            // links dentro do mesmo site já os encontram.
            else if (browser.GetBrowser() is { } cef) _ = Task.Run(() => injector.EnsureHost(cef, address));
        });
        browser.ConsoleMessage += (_, e) =>
        {
            // Chega na thread do CEF; as definições avisam a interface, por isso guarda-se na thread da janela.
            if (PopupPolicy.IsActivationReport(e.Message, out var clickedLink, out var interactive))
            {
                Volatile.Write(ref lastActivation, new UserActivation(clickedLink, DateTime.UtcNow, interactive));
                Dispatcher.InvokeAsync(() => OnPageInteraction(tab, browser.Address));
                return;
            }
            if (VideoEngine.Parse(e.Message) is { } videoReport)
            {
                Dispatcher.InvokeAsync(() => OnVideoReport(tab, browser.Address ?? tab.Address, videoReport));
                return;
            }
            if (LinkPreview.IsReport(e.Message, out var glanceUrl))
            {
                // Alt+clique num link: só da página à vista (a ativa ou uma das metades do ecrã dividido) e só com um Alt
                // premido de verdade há pouco, um cartão por Alt — a página (ou um iframe) pode escrever a mesma
                // mensagem na consola, mas não carregar no teclado.
                Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(tab, Tabs.Active) && _split?.PaneOf(tab) is null) return;
                    if (_altTap.TakeGesture(DateTime.UtcNow, LinkPreview.GestureWindow)) ShowLinkPreview(glanceUrl);
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
            NudgeMediaPoll(tab);
            var url = tab.Address;
            if (e.NewValue is not string { Length: > 0 } title)
            {
                tab.Title = VideoEngine.MainEngineTitle(null, url);
                return;
            }
            // Ficheiro de vídeo/áudio aberto diretamente: o Chromium dá o endereço como título; fica o nome do
            // ficheiro, como no motor de vídeo (e no histórico, o mesmo título nos dois motores).
            title = VideoEngine.MainEngineTitle(title, url);
            tab.Title = title;
            if (!IsPrivate) AppData.UpdateTitleLater(url, title);
        };
        browser.LoadingStateChanged += (_, e) => Dispatcher.InvokeAsync(() =>
        {
            // A meio da troca para o motor de vídeo, o estado da aba já é o dele.
            if (_videoViews.ContainsKey(tab)) return;
            tab.IsLoading = e.IsLoading;
            if (!e.IsLoading) NudgeMediaPoll(tab);
            tab.Crossing.MainHistoryChanged(e.CanGoBack);
            tab.CanGoBack = e.CanGoBack;
            tab.CanGoForward = tab.Crossing.CanGoForward(inVideo: false, e.CanGoForward);
        });
        browser.LoadError += (_, e) => RecoverMainFrame(browser, e, IsPrivate, mainHistory);
        // Lida uma vez aqui, na thread da janela (o FrameLoadStart chega noutra).
        var permissionStore = SitePermissions;
        browser.FrameLoadStart += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            // Documento novo: a captura autorizada ao anterior acabou com ele (Tabs/UnseenWindow.cs, ajuste e8).
            CaptureSessions.Ended(e.Browser.Identifier);
            ResetUnstoredEnginePermissions(e.Browser, e.Url, permissionStore);
            KitTabNavigated(e.Browser.Identifier);
            StartupTrace.MarkOnce("aba.1.commit");
            // Página nova sem pedido de rede (servida pelo service worker, ou reposta da cache de Voltar/Avançar): a
            // contagem do Escudo recomeça aqui, no commit.
            // Voltar/Avançar sem pedido é a página reposta da cache (bfcache): volta a contagem e o ícone que ela tinha, e
            // o que só se faz no fim de um carregamento (que não volta a acontecer) faz-se aqui.
            var requests = Volatile.Read(ref mainRequests);
            var leaving = Volatile.Read(ref committedUrl);
            var arriving = e.Url;
            Volatile.Write(ref committedUrl, arriving);
            tab.Observatory.Commit(arriving);
            // Documento novo: o canal do cofre prepara-o (mundo isolado; MainWindow.Vault.cs).
            VaultPageCommitted(tab, browser);
            if (requests == mainRequestsAtCommit)
            {
                var restored = e.TransitionType.HasFlag(TransitionType.ForwardBack);
                Dispatcher.InvokeAsync(() =>
                {
                    tab.LeavePage(leaving);
                    if (restored && tab.ReturnToPage(arriving)) OnPageRestored(tab, browser, arriving);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            mainRequestsAtCommit = requests;
        };
        var firstLoad = new TaskCompletionSource();
        _firstLoads.Add(browser, firstLoad);
        browser.LoadError += (_, e) =>
        {
            if (e.Frame.IsMain) firstLoad.TrySetResult();
        };
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            if (!firstLoad.Task.IsCompleted) StartupTrace.Mark("aba.carregada");
            firstLoad.TrySetResult();
            var url = e.Url;
            // O 1.º documento de uma aba nasce antes de a sessão do canal do cofre poder ouvir o commit dele.
            VaultPageCommitted(tab, browser);
            // Aba parada: os registos de sites antigos a mais saem agora (nunca a meio de uma navegação).
            if (!e.Browser.IsLoading) injector.Tidy(url);
            Dispatcher.InvokeAsync(() =>
            {
                // Acordou de um sono com a rolagem guardada (ajuste e13, MainWindow.Sleep.cs).
                RestoreSleptScroll(tab, browser, url);
                DetectSiteTint(tab, browser, url);
                DetectReaderable(tab, browser, url);
                ApplyHiddenElements(browser, url);
                CaptureThumbnail(tab, browser, url);
                _ = ApplyGenericCosmeticsAsync(browser, url);
                SettleChainLater(tab);
                if (SelfTest.IsTestPage(url)) _ = RunSelfTestAsync(tab, browser, url);
                // 1.º documento da aba (nasceu sem os scripts precoces) ou qualquer outro: a deteção de vídeo que não
                // toca corre agora; se o script precoce já corre na página, não faz nada.
                if (VideoEngine.CanHost(url)) PageScript.Run(browser, VideoEngine.LateWatchScript);
                else _ = ProbePlatformCodecsAsync(browser, url);
            });
            // Um ficheiro de vídeo ou áudio aberto diretamente acaba «abortado» (-3) depois do commit: conta como visita.
            if (!HistoryVisit.FromMainEngine(e.HttpStatusCode, committed: Volatile.Read(ref committedUrl) == url)) return;
            // A página do auto-teste é do próprio Éteris: não entra no histórico.
            if (!IsPrivate && !SelfTest.IsTestPage(url)) Dispatcher.InvokeAsync(() =>
            {
                var title = tab.Title;
                AppData.QueueWrite(h => h.Record(url, title, DateTime.UtcNow));
                RememberFavicon(tab, url);
            });
        };
        browser.StatusMessage += (_, e) =>
        {
            // Rato sobre um link: os scripts do site de destino registam-se já, com a aba parada.
            if (e.Browser is { } hovered && e.Value is { Length: > 0 } link) _ = Task.Run(() => injector.EnsureHostIfMissing(hovered, link));
            var text = Eteris.Browser.Omnibox.LinkStatus.Format(e.Value);
            Dispatcher.InvokeAsync(() => ShowLinkStatus(tab, text));
        };
        browser.IsBrowserInitializedChanged += (_, _) =>
        {
            if (browser.IsBrowserInitialized) StartupTrace.Mark("aba.cef");
            if (browser.IsBrowserInitialized && browser.GetBrowser() is { } cef) injector.Start(cef);
            if (browser.IsBrowserInitialized && tab.IsMuted) browser.GetBrowserHost()?.SetAudioMuted(true);
            // Abas abertas em segundo plano: a janela do Chromium fica logo escondida (ver SetPageWindowVisible).
            if (browser.IsBrowserInitialized) Dispatcher.InvokeAsync(LayoutBrowsers);
        };
        _browsers[tab] = browser;
        BrowserHost.Children.Add(browser);
        WatchPaneFocus(tab, browser);
        if (IsSplit) LayoutBrowsers();
        StartupTrace.Mark("aba.criada");
        // Sem UpdateLayout aqui: dispor a aba nova logo a seguir a criá-la (para o CefSharp não desfazer o BirthSize no fim
        // da criação) fazia a 1.ª aba nova da sessão aparecer branca 2–3 fotogramas depois de a antiga sair (medido a
        // 60 fps no ramo motor: 2 em 3 sessões com ele, 0 em 4 sem ele). Com a disposição normal do WPF a janela do CEF
        // passa por 0×0 no fim da criação e volta ao tamanho final na disposição seguinte, antes da 1.ª pintura; quem
        // garante que a aba só sobe já pintada é a espera da troca (PageSwap.FirstPaintScript).
    }

    private static bool SameHost(string a, string b) => BrowserTab.SameHost(a, b);

    // Página reposta da cache de Voltar/Avançar: não há fim de carregamento (FrameLoadEnd), por isso a cor do site, o
    // modo de leitura e a miniatura atualizam-se aqui. O DOM é o mesmo de antes: as regras cosméticas já lá estão.
    private void OnPageRestored(BrowserTab tab, ChromiumWebBrowser browser, string url)
    {
        if (browser.IsDisposed || tab.Address != url) return;
        DetectSiteTint(tab, browser, url);
        DetectReaderable(tab, browser, url);
        CaptureThumbnail(tab, browser, url);
    }

    // Comum a todas as páginas do separador (registado uma vez, antes da 1.ª navegação). Nas páginas internas (nova
    // aba, favoritos, histórico) só o Alt+clique: sem ele o Chromium transferia o link.
    private static readonly string TabBaseScript =
        HostScriptRegistry.ForProtocols(["http:", "https:"], VideoEngine.WatchScript + "\n;\n" + PopupPolicy.ActivationScript)
        + "\n;\n" + HostScriptRegistry.ForProtocols(["http:", "https:", "eteris:"], LinkPreview.AltClickScript);

    private static string SiteEarlyKey(string url) => WebHost(url) is { } host && AppData.IsDark(host) ? "escuro" : "";

    // Por site: regras cosméticas (escudo ligado) e modo escuro (se pedido). null quando o site não precisa de nada.
    private string? SiteEarlyScript(string url)
    {
        var dark = DarkPages.IsCandidate(url) && WebHost(url) is { } host && AppData.IsDark(host);
        var script = SiteCosmeticScript(url) + (dark ? DarkPages.EarlyScript : "");
        return script.Length == 0 ? null : script;
    }

    // Antes de uma navegação pedida pelo Éteris: os scripts do site de destino registam-se com a aba parada e só depois o
    // pedido sai. Nunca espera mais de 150 ms.
    private async Task ScriptsReadyFor(BrowserTab tab, ChromiumWebBrowser browser, string url)
    {
        if (!_scriptlets.TryGetValue(tab, out var injector) || browser.IsDisposed || browser.GetBrowser() is not { } cef) return;
        var ready = injector.EnsureHost(cef, url);
        if (!ready.IsCompleted) await Task.WhenAny(ready, Task.Delay(150));
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TabsViewModel.Active)) return;
        CloseBrowserMenu();
        OnActiveTabChanging();
        OnSplitActiveChanged();
        MoveFocusOffHiddenPage();
        // A reautenticação pedida por uma página do cofre desiste quando a aba sai da frente.
        CancelVaultRequests();
        CloseGlanceOnTabChange();
        // O cartão do cadeado mostrava o site da aba anterior por cima da nova.
        SiteInfoPopup.IsOpen = false;
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
            or nameof(BrowserTab.Group) or nameof(BrowserTab.IsHiddenByGroup) or nameof(BrowserTab.CustomTitle)
            or nameof(BrowserTab.Emoji) or nameof(BrowserTab.ColorIndex) or nameof(BrowserTab.Note) or nameof(BrowserTab.IsLocked)) ScheduleSessionSave();
        if (e.PropertyName is nameof(BrowserTab.IsPinned) or nameof(BrowserTab.IsHiddenByGroup) or nameof(BrowserTab.IsGroupHead)) UpdateTabWidth();
        // Uma aba quente que começa a tocar som arrefece já (Tabs/WarmTabs.cs).
        if (e.PropertyName is nameof(BrowserTab.IsAudible) or nameof(BrowserTab.Media) && _warmPages.Count > 0) ReviewWarmPages();
        if (!ReferenceEquals(sender, Tabs.Active)) return;
        if (e.PropertyName == nameof(BrowserTab.Address))
        {
            // Página nova na aba à vista: o cartão do cadeado ficava aberto com o site anterior.
            SiteInfoPopup.IsOpen = false;
            SyncOmnibox(); UpdateShieldButton(); UpdateStar(); UpdateZoomBadge();
        }
        if (e.PropertyName == nameof(BrowserTab.IsLoading)) SyncLoading();
        if (e.PropertyName == nameof(BrowserTab.BlockedCount)) UpdateShieldButton();
        if (e.PropertyName == nameof(BrowserTab.Tint)) ApplyTint();
        if (e.PropertyName is nameof(BrowserTab.UsesVideoEngine) or nameof(BrowserTab.VideoOffer)) UpdateVideoEngineBadge();
    }

    private void SyncLoading()
        => AuroraBar.Visibility = Tabs.Active?.IsLoading == true ? Visibility.Visible : Visibility.Collapsed;

    // A 30 fotogramas por segundo chega para um brilho que desliza; parada com a janela minimizada.
    private bool _auroraRunning;

    private void AnimateAurora(bool run)
    {
        if (run == _auroraRunning || AuroraBar.Fill is not LinearGradientBrush { RelativeTransform: TranslateTransform shift }) return;
        _auroraRunning = run;
        if (!run)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        var slide = new DoubleAnimation(0, 0.5, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(slide, 30);
        shift.BeginAnimation(TranslateTransform.XProperty, slide);
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
        if (tab is null) return;
        if (tab.IsLocked)
        {
            ShowToast($"«{tab.DisplayTitle}» está trancada — destranque-a no menu da aba para a fechar");
            return;
        }
        var before = Tabs.RecentlyClosed;
        if (Tabs.Close(tab)) Close();
        else RememberClosedTab(tab, before);
    }

    // «Feito»: a aba sai da faixa e fica em «Feitas hoje» no Radar (nunca na janela privada).
    private void MarkTabDone(BrowserTab tab)
    {
        if (!Tabs.Tabs.Contains(tab)) return;
        var isNew = NewTabPage.IsNewTab(tab.Address);
        var recorded = !IsPrivate && !isNew && AppData.MarkDone(tab.Address, tab.DisplayTitle);
        var snapshot = Tabs.Snapshot(tab);
        var before = Tabs.RecentlyClosed;
        // Sem lugar em «Feitas hoje» (janela privada, ou a base de dados não gravou), a aba vai para as fechadas
        // recentes: «Feito» nunca pode ser um fecho sem volta, pior do que «Fechar».
        Tabs.CloseDone(tab, remember: !recorded);
        if (Tabs.Tabs.Count == 0) Tabs.NewTab(NewTabUrl);
        if (!IsPrivate && !isNew && !recorded)
        {
            OfferReopen(before, $"Feito: «{tab.DisplayTitle}» — não ficou em «Feitas hoje» (base de dados indisponível)",
                $"Feito {ToastPolicy.Quote(tab.DisplayTitle)}");
            return;
        }
        var label = $"Feito {ToastPolicy.Quote(tab.DisplayTitle)}";
        if (!recorded)
        {
            // Janela privada ou aba nova: nada ficou gravado, a aba volta como estava.
            if (!isNew) ShowUndoToast($"Feito: «{tab.DisplayTitle}»", label, () => Tabs.PutBack(snapshot) is not null, [snapshot.Address]);
            return;
        }
        var today = DoneToday().Count;
        ShowUndoToast(today > 1 ? $"Feito: «{tab.DisplayTitle}» — {today} feitas hoje (ver no Radar)" : $"Feito: «{tab.DisplayTitle}»",
            label, () => PutBackDone(snapshot), [snapshot.Address]);
    }

    private IReadOnlyList<DoneTab> DoneToday()
        => IsPrivate ? [] : AppData.DoneSince(DoneTabsStore.StartOfLocalDayUtc(DateTime.Now));

    private const double PinnedTabWidth = 44;

    // Largura de cada aba e da janela de rolagem da faixa. As abas encolhem até à largura mínima legível; a partir
    // daí a faixa (TabScroll) fica com a largura que sobra e rola — nenhuma aba desaparece nem fica ilegível.
    private void UpdateTabWidth()
    {
        var pinned = Tabs.PinnedCount;
        var (groupsReserved, visible) = GroupLayout();
        var badge = PrivateBadge.Visibility == Visibility.Visible ? PrivateBadge.ActualWidth + PrivateBadge.Margin.Right : 0;
        var spaces = SpaceStrip.Visibility == Visibility.Visible ? SpaceStrip.ActualWidth + SpaceStrip.Margin.Right : 0;
        var strip = Math.Max(0, TabStripHost.ActualWidth - TabStripReserved - badge - spaces + (Compact ? BrandMark.Width : 0));
        TabScroll.MaxWidth = strip;
        TabWidth = TabLayout.TabWidth(strip - pinned * PinnedTabWidth - groupsReserved, visible);
        UpdateTabArrows();
        RevealActiveTab();
    }

    private void OnWindowStateChanged()
    {
        // Ajuste e8 (MainWindow.Unseen.cs): a página à vista esconde-se ao minimizar antes de o CefSharp a encolher a 0×0.
        ReviewUnseen();
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        // Maximizada com moldura, o Windows estende a janela para lá do ecrã; sem moldura (ecrã inteiro) não.
        RootBorder.Margin = maximized && WindowStyle != WindowStyle.None
            ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4)
            : new Thickness(0);
        MaximizeButton.Content = MaximizeButton2.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = MaximizeButton2.ToolTip = maximized ? "Restaurar" : "Maximizar";
        CloseSuggestions();
        // Janela minimizada: as abas quentes arrefecem (nada se vê).
        ReviewWarmPages();
    }

    private void DisposeBrowsers()
    {
        ForgetWarmPages();
        foreach (var injector in _scriptlets.Values) injector.Dispose();
        _scriptlets.Clear();
        foreach (var browser in _browsers.Values)
        {
            ForgetCnameQuota(browser);
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
        AltKeyDown(key, ctrl, shift);

        if (RadarOpen)
        {
            e.Handled = HandleRadarKey(key, ctrl, shift, alt);
            return;
        }

        // Alt em modo foco: decide-se ao largar (AltKeyUp) — Alt sozinho mostra as barras, Alt+← não.
        if (key is Key.LeftAlt or Key.RightAlt && !ctrl && !shift && BarsAutoHide)
        {
            e.Handled = false;
            return;
        }

        // O menu do Éteris é um Popup sem pai: as suas teclas chegam aqui pela raiz do menu (ver ToggleBrowserMenu).
        if (BrowserMenuOpen)
        {
            e.Handled = HandleBrowserMenuKey(key, ctrl, shift, alt);
            return;
        }

        if (key == Key.Escape && RenameOpen)
        {
            CloseRename();
            ReturnFocusToPage();
        }

        else if (key == Key.Escape && ShortcutsOpen) CloseShortcuts();
        else if (key == Key.Escape && ObservatoryOpen) CloseObservatory();
        else if (key == Key.Escape && GlanceOpen) CloseGlance();
        // Ctrl+Z do Éteris: só aqui (teclas da moldura) e fora dos campos de texto — com o foco na página, o Ctrl+Z
        // nem passa por esta janela e a página desfaz o que é seu.
        else if (ctrl && !shift && !alt && key == Key.Z && !TextInputHasFocus()) UndoLast();
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
        // A exceção: Ctrl+Alt+1…9 e Ctrl+Alt+←/→ mudam de Espaço — mas só com o Alt da esquerda físico (o AltGr é
        // Ctrl+Alt da direita para o Windows, e AltGr+2 tem de continuar a escrever «@»).
        if (ctrl && alt) return WorkspaceShortcut.Resolve(key, shift, IsLeftAltDown()) is { } move ? () => RunWorkspaceShortcut(move) : null;
        // Alt+F e Alt+E, como no Chrome e no Brave.
        if (alt && !ctrl && !shift && key is Key.F or Key.E) return () => ToggleBrowserMenu(byKeyboard: true);
        if (key == Key.F1 || (ctrl && key is Key.OemQuestion or Key.Divide)) return ToggleShortcuts;
        if (ctrl && shift && key == Key.A) return ToggleRadar;
        if (ctrl && key == Key.K) return TogglePalette;
        if (ctrl && key == Key.J) return ToggleDownloads;
        if (ctrl && key == Key.D) return ToggleBookmark;
        if ((ctrl && key == Key.F) || key == Key.F3) return OpenFind;
        if (key == Key.F11) return ToggleFocusMode;
        if (key == Key.F9) return ToggleReader;
        if (key == Key.F12 || (ctrl && shift && key == Key.I)) return ShowDevTools;
        if (ctrl && shift && key == Key.U) return ToggleObservatory;
        if (ctrl && key == Key.U) return ViewSource;
        if (ctrl && key == Key.H) return OpenHistory;
        if (ctrl && shift && key == Key.L) return ShowLinkHints;
        if (ctrl && shift && key == Key.E && !IsPrivate) return ToggleNotes;
        if (ctrl && shift && key == Key.B) return () => ToggleSidebar();
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
        // F6 com a bolha «Guardar a palavra-passe?» à vista: primeiro a bolha, depois a barra de endereço.
        if (key == Key.F6 && !ctrl && !alt) return () => { if (!FocusSaveBubble()) FocusOmnibox(); };
        if ((ctrl && key == Key.L) || (alt && key == Key.D)) return FocusOmnibox;
        if ((ctrl && shift && key == Key.R) || (ctrl && key == Key.F5)) return () => ReloadActive(ignoreCache: true);
        if ((ctrl && key == Key.R) || key == Key.F5) return () => ReloadActive();
        if (alt && key == Key.Left) return GoBack;
        if (alt && key == Key.Right) return GoForward;
        if (ctrl && key >= Key.D1 && key <= Key.D9) return () => Tabs.SelectByNumber(key - Key.D0);
        if (ctrl && key >= Key.NumPad1 && key <= Key.NumPad9) return () => Tabs.SelectByNumber(key - Key.NumPad0);
        return null;
    }

    // Tecla largada com o foco na página (thread do CEF): só serve ao «Alt sozinho».
    private void KeyUpFromPage(int virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        Dispatcher.InvokeAsync(() => AltKeyUp(key));
    }

    // Tecla premida com o foco na página (thread do CEF): os atalhos do Éteris são consumidos e corridos na janela;
    // o Esc chega à página na mesma, e também fecha a folha de atalhos, sai do ecrã inteiro ou para o carregamento.
    // Com o menu do Éteris aberto (o foco do Win32 ainda na página, por um instante), a tecla é do menu e não da página:
    // decide BrowserMenuKeys.FromPage (puro) e o trabalho vai para a thread da janela. Não se lê nada do WPF aqui.
    // from: a aba de onde veio a tecla (null: um site fixado na barra lateral).
    private bool ShortcutFromPage(int virtualKey, bool ctrl, bool shift, bool alt, BrowserTab? from = null)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        Dispatcher.InvokeAsync(() => AltKeyDown(key, ctrl, shift));
        var shortcut = Shortcut(key, ctrl, shift, alt);
        if (_browserMenuOpenForPage)
        {
            var decision = BrowserMenuKeys.FromPage(key, ctrl, shift, alt, shortcut is not null);
            // Nenhuma tecla chega à página com o menu aberto, nem os modificadores sozinhos (como no Chrome).
            Dispatcher.InvokeAsync(() => HandleBrowserMenuKeyFromPage(decision, shortcut, key));
            return true;
        }
        // A lista de sugestões de palavras-passe aberta (ou ↓ num campo com sugestões): ↑ ↓ Enter Esc são dela e a
        // página nunca os vê (MainWindow.Vault.cs).
        if (SuggestionKeyFromPage(key, ctrl, shift, alt, from) is { } listKey) return listKey;
        if (shortcut is { } action)
        {
            Dispatcher.InvokeAsync(action);
            return true;
        }
        if (key == Key.Escape)
            Dispatcher.InvokeAsync(() =>
            {
                if (RadarOpen) CloseRadar();
                else if (ShortcutsOpen) CloseShortcuts();
                else if (ObservatoryOpen) CloseObservatory();
                else if (_pageFullscreen) ExitPageFullscreen();
                else if (Tabs.Active?.IsLoading == true) StopActive();
            });
        return false;
    }

    // A página recebeu o foco (clique, Tab): no ecrã dividido, a metade em que se clicou passa a ser a ativa.
    private void OnPageFocused(BrowserTab tab)
    {
        CloseSuggestions();
        // O foco passou para outra aba (a outra metade do ecrã dividido): o campo do cofre da anterior deixa de contar.
        if (_vaultFocus is { } vault && !ReferenceEquals(vault.Tab, tab)) ForgetVaultFocus();
        if (IsSplit && !ReferenceEquals(Tabs.Active, tab) && _split!.PaneOf(tab) is not null) Tabs.Active = tab;
    }

    private void TabStripHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidth();

    private void BrowserHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => BrowserHost.Clip = new RectangleGeometry(new Rect(e.NewSize), _pageRadius, _pageRadius);

    private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;
        FocusFrame();
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
        {
            // Ctrl/Shift+clique selecionam sem mudar de aba (nem começam um arrasto).
            if (HandleSelectionClick(tab))
            {
                _dragTab = null;
                e.Handled = true;
                return;
            }
            Tabs.Active = tab;
        }
    }

    // Um clique numa aba (ou no «x» dela) leva o teclado para a moldura, como no Chrome: sem isto o foco ficava na
    // página do Chromium (ou na que acabou de fechar) e o Ctrl+Z, o Ctrl+T e os outros atalhos iam para lá. A página
    // volta a ter o teclado com um clique nela.
    private void FocusFrame()
    {
        if (RenameOpen) return;
        if (_overlay is { IsActive: true }) Activate();
        Keyboard.Focus(this);
        // O Keyboard.Focus do WPF não tira o foco do Win32 à janela do Chromium (filha, noutra thread): pede-se à mão.
        SetFocus(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint window);

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
