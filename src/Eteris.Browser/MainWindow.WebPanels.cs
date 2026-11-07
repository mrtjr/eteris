using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser;

// Sites fixados na barra lateral: cada um abre dentro da barra numa instância própria do Chromium (janela própria,
// como as abas), no mesmo contexto da janela — cookies e sessões partilhados com as abas — e com o Escudo. Os
// motores vivem enquanto a barra estiver aberta (mudar de separador só os esconde, para um chat não perder o
// sítio) e são largados quando a barra fecha. Nunca na janela privada. Lista em Sidebar/WebPanels.cs.
public partial class MainWindow
{
    private IReadOnlyList<WebPanel> _webPanels = [];
    private readonly Dictionary<string, ChromiumWebBrowser> _webPanelBrowsers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptletInjector> _webPanelInjectors = new(StringComparer.Ordinal);

    private static WebPanelStore WebPanelsFile => new(Path.Combine(AppPaths.Root, "paineis.json"));

    // Uma janela mudou os sites fixados: as outras (todas na mesma thread) voltam a ler o ficheiro.
    private static event Action<MainWindow>? WebPanelsChanged;

    private void LoadWebPanels()
    {
        _webPanels = IsPrivate ? [] : WebPanelsFile.Load();
        Closed += (_, _) => ReleaseWebPanelBrowsers();
        if (IsPrivate) return;
        void OnChanged(MainWindow from)
        {
            if (!ReferenceEquals(from, this)) ReloadWebPanels();
        }
        WebPanelsChanged += OnChanged;
        Closed += (_, _) => WebPanelsChanged -= OnChanged;
    }

    // Os sites fixados mudaram noutra janela: a lista, os motores (de sites tirados ou com outra vista) e a coluna.
    private void ReloadWebPanels()
    {
        var fresh = WebPanelsFile.Load();
        foreach (var url in _webPanelBrowsers.Keys.ToList())
            if (WebPanels.Find(fresh, url) is not { } now || now.Mobile != (WebPanels.Find(_webPanels, url)?.Mobile == true))
                ReleaseWebPanelBrowser(url);
        _webPanels = fresh;
        BuildSidebarRail();
        if (!_sidebarOpen || _sidebarPanel?.Url is not { } shown) return;
        if (WebPanels.Find(fresh, shown) is null)
        {
            if (SidebarAvailable.Count > 0) ShowSidebarPanel(SidebarAvailable[0], focus: false);
            else CloseSidebar();
        }
        else if (!_webPanelBrowsers.ContainsKey(shown)) ShowWebPanel(_sidebarPanel, focus: false);
    }

    // Muda a lista a partir do ficheiro (não da cópia desta janela), grava e avisa as outras janelas.
    private IReadOnlyList<WebPanel>? UpdateWebPanels(Func<IReadOnlyList<WebPanel>, IReadOnlyList<WebPanel>?> change)
    {
        IReadOnlyList<WebPanel>? list;
        try { list = WebPanelsFile.Update(change); }
        catch (Exception)
        {
            ShowToast("Não foi possível guardar os sites fixados");
            return null;
        }
        if (list is null) return null;
        _webPanels = list;
        BuildSidebarRail();
        WebPanelsChanged?.Invoke(this);
        return list;
    }

    private IEnumerable<SidebarPanel> WebSidebarPanels => _webPanels.Select(WebPanels.ToPanel);

    private bool IsPinnedSite(string? url) => WebPanels.Contains(_webPanels, url);

    // «Fixar este site na barra lateral»: a página aberta passa a separador e abre logo.
    private void PinCurrentSite()
    {
        if (IsPrivate || Tabs.Active is not { } tab || WebHost(tab.Address) is null)
        {
            ShowToast(IsPrivate ? "Os sites fixados não ficam na janela privada" : "Só se fixam páginas da web");
            return;
        }
        var current = WebPanelsFile.Load();
        if (WebPanels.Add(current, tab.Address, tab.Title) is null)
        {
            ShowToast(WebPanels.Contains(current, tab.Address) ? "Este site já está na barra lateral" : $"A barra lateral já tem {WebPanels.Max} sites fixados");
            return;
        }
        var (address, title) = (tab.Address, tab.Title);
        if (UpdateWebPanels(list => WebPanels.Add(list, address, title)) is null) return;
        OpenSidebar(SidebarPanels.WebKey(WebPanels.Normalize(tab.Address)!));
        ShowToast(WebPanels.HasCredentials(address)
            ? $"«{SidebarLists.HostOf(tab.Address)}» fixado sem o utilizador e a palavra-passe do endereço"
            : $"«{SidebarLists.HostOf(tab.Address)}» fixado na barra lateral");
    }

    private void UnpinSite(string url)
    {
        ReleaseWebPanelBrowser(url);
        UpdateWebPanels(list => WebPanels.Remove(list, url));
        if (_sidebarPanel?.Url == url)
        {
            if (_sidebarOpen && SidebarAvailable.Count > 0) ShowSidebarPanel(SidebarAvailable[0], focus: false);
            else CloseSidebar();
        }
        ShowToast("Site tirado da barra lateral");
    }

    private void MovePinnedSite(string url, int delta) => UpdateWebPanels(list => WebPanels.Move(list, url, delta));

    // «Vista móvel»: o motor nasce de novo com o agente de telemóvel (ou sem ele) e o site volta a carregar.
    private void SetPinnedSiteMobile(string url, bool mobile)
    {
        ReleaseWebPanelBrowser(url);
        UpdateWebPanels(list => WebPanels.SetMobile(list, url, mobile));
        if (_sidebarOpen && _sidebarPanel?.Url == url) ShowWebPanel(_sidebarPanel, focus: false);
        ShowToast(mobile ? "Vista móvel ligada — o site vê um telemóvel" : "Vista móvel desligada");
    }

    // ---------- O motor de cada site ----------

    private void ShowWebPanel(SidebarPanel panel, bool focus)
    {
        var url = panel.Url!;
        var browser = EnsureWebPanelBrowser(url);
        foreach (var (other, view) in _webPanelBrowsers)
        {
            var visible = other == url;
            view.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (view.IsBrowserInitialized) SetPageVisible(view, visible);
        }
        SidebarTitle.Text = browser.Title is { Length: > 0 } title ? title : panel.Title;
        if (focus && browser.IsBrowserInitialized) Keyboard.Focus(browser);
    }

    // Ao mudar para um separador que não é um site, os motores ficam escondidos (e a página marcada como escondida:
    // deixa de desenhar, mas continua a receber mensagens).
    private void HideWebPanels()
    {
        foreach (var view in _webPanelBrowsers.Values)
        {
            view.Visibility = Visibility.Hidden;
            if (view.IsBrowserInitialized) SetPageVisible(view, false);
        }
    }

    private ChromiumWebBrowser EnsureWebPanelBrowser(string url)
    {
        if (_webPanelBrowsers.TryGetValue(url, out var existing) && !existing.IsDisposed) return existing;
        // O Escudo completo, como nas abas: além do bloqueio de rede, os scriptlets e as regras cosméticas do site
        // (registados pelo DevTools antes dos scripts da página) e, no fim de cada carregamento, as genéricas.
        // O Alt+clique espreita como nas abas (sem o script, o Chromium transferia o link para Transferências).
        var mobile = WebPanels.Find(_webPanels, url)?.Mobile == true;
        var injector = new ScriptletInjector(AppData.IsShieldOn, page => !LinkPreview.WantsAltClick(page) ? null
            : SiteCosmeticScript(page) is { Length: > 0 } css ? css + "\n;\n" + LinkPreview.AltClickScript : LinkPreview.AltClickScript,
            // Vista móvel também para o JavaScript da página (navigator.*), não só nos cabeçalhos dos pedidos.
            onSession: mobile ? ApplyMobileUserAgent : null);
        _webPanelInjectors[url] = injector;
        var panelHistory = new MainFrameHistory();
        var browser = new ChromiumWebBrowser(url)
        {
            Visibility = Visibility.Hidden,
            // Pop-ups viram abas (só com gesto).
            LifeSpanHandler = new PopupToTabHandler(request =>
            {
                if (request.UserGesture && PageMenu.IsWebLink(request.TargetUrl))
                    Dispatcher.InvokeAsync(() => Tabs.NewTab(request.TargetUrl, activate: request.Foreground));
            }),
            DownloadHandler = DownloadHandler,
            PermissionHandler = PermissionHandler,
            RequestHandler = new PrivacyRequestHandler(() => ShieldEngine.Current, AppData.IsShieldOn,
                onNavigate: (cefBrowser, page) => injector.Prepare(cefBrowser, page),
                onBlocked: _ => ShieldStats.CountBlocked(persist: true),
                // O processo do site morreu: a página de erro (com «Tentar novamente») em vez de um retângulo vazio.
                onCrashed: () => Dispatcher.InvokeAsync(() => OnWebPanelCrashed(url)),
                // Vista móvel: os pedidos deste motor saem com o agente de um telemóvel.
                userAgent: mobile ? WebPanels.MobileUserAgent : null,
                // Rede local: o que o site já tem autorizado nas abas vale também na barra lateral.
                localNetworkAllowed: IsLocalNetworkAllowed,
                onMainNavigation: nav => Volatile.Write(ref panelHistory.Last, nav)),
        };
        // Os atalhos do Éteris chegam do painel como de qualquer página, mas recarregar, procurar, imprimir e as
        // ferramentas de programador agem sobre o painel (WebPanelKeys), não sobre a aba por trás.
        browser.FindHandler = NewFindHandler();
        browser.KeyboardHandler = new ShortcutKeyboardHandler((key, ctrl, shift, alt) => ShortcutFromWebPanel(browser, key, ctrl, shift, alt), KeyUpFromPage);
        // Ecrã inteiro pedido pelo site (um vídeo): não cabe na barra — sai e diz como o ver numa aba.
        browser.DisplayHandler = new FaviconHandler((_, _) => { }, fullscreen =>
        {
            if (fullscreen) Dispatcher.InvokeAsync(() => OnWebPanelFullscreen(browser));
        });
        // Como nas abas: o site que não fala HTTPS volta a http:// («HTTPS primeiro»); as outras falhas dão a página
        // de erro (com «Tentar novamente») em vez de um painel em branco.
        browser.LoadError += (_, e) => RecoverMainFrame(browser, e, isPrivate: false, panelHistory);
        browser.MenuHandler = new PageMenuHandler(IsPrivate, (context, _, _) => Dispatcher.InvokeAsync(() => ShowWebPanelMenu(browser, context)));
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            var loaded = e.Url;
            Dispatcher.InvokeAsync(() =>
            {
                if (browser.IsDisposed) return;
                ApplyHiddenElements(browser, loaded);
                _ = ApplyGenericCosmeticsAsync(browser, loaded);
            });
        };
        browser.ConsoleMessage += (_, e) =>
        {
            if (!LinkPreview.IsReport(e.Message, out var glanceUrl)) return;
            // Como nas abas: só do site à vista na barra e só com um Alt verdadeiro, um cartão por Alt.
            Dispatcher.InvokeAsync(() =>
            {
                if (browser.IsDisposed || !browser.IsVisible) return;
                if (_altTap.TakeGesture(DateTime.UtcNow, LinkPreview.GestureWindow)) ShowLinkPreview(glanceUrl);
            });
        };
        browser.TitleChanged += (_, e) => Dispatcher.InvokeAsync(() =>
        {
            if (_sidebarPanel?.Url == url && e.NewValue is string { Length: > 0 } title) SidebarTitle.Text = title;
        });
        browser.IsBrowserInitializedChanged += (_, _) =>
        {
            if (!browser.IsBrowserInitialized) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (browser.IsDisposed) return;
                SetPageVisible(browser, _sidebarPanel?.Url == url && _sidebarOpen);
            });
        };
        _webPanelBrowsers[url] = browser;
        SidebarWebHost.Children.Add(browser);
        return browser;
    }

    private static Task ApplyMobileUserAgent(CefSharp.DevTools.DevToolsClient client)
        => client.Emulation.SetUserAgentOverrideAsync(WebPanels.MobileUserAgent, null, WebPanels.MobileNavigatorPlatform,
            new CefSharp.DevTools.Emulation.UserAgentMetadata
            {
                Brands =
                [
                    new CefSharp.DevTools.Emulation.UserAgentBrandVersion { Brand = "Chromium", Version = WebPanels.MobileMajorVersion },
                    new CefSharp.DevTools.Emulation.UserAgentBrandVersion { Brand = "Not_A Brand", Version = "24" },
                ],
                Platform = WebPanels.MobilePlatform,
                PlatformVersion = WebPanels.MobilePlatformVersion,
                Architecture = string.Empty,
                Model = WebPanels.MobileModel,
                Mobile = true,
            });

    // Tecla premida com o foco num site fixado (thread do CEF).
    private bool ShortcutFromWebPanel(ChromiumWebBrowser browser, int virtualKey, bool ctrl, bool shift, bool alt)
    {
        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        if (WebPanelKeys.Resolve(key, ctrl, shift, alt) is not { } command) return ShortcutFromPage(virtualKey, ctrl, shift, alt);
        Dispatcher.InvokeAsync(() => AltKeyDown(key, ctrl, shift));
        Dispatcher.InvokeAsync(() =>
        {
            if (browser.IsDisposed) return;
            switch (command)
            {
                case WebPanelKey.Reload: ReloadWebPanel(browser, ignoreCache: false); break;
                case WebPanelKey.HardReload: ReloadWebPanel(browser, ignoreCache: true); break;
                case WebPanelKey.Find: OpenFind(browser); break;
                case WebPanelKey.Print: browser.Print(); break;
                case WebPanelKey.DevTools: browser.GetBrowserHost()?.ShowDevTools(); break;
            }
        });
        return true;
    }

    // Numa página de erro, recarregar volta a tentar o endereço que falhou (como nas abas).
    private static void ReloadWebPanel(ChromiumWebBrowser browser, bool ignoreCache)
    {
        if (ErrorPage.FailedUrl(browser.Address ?? string.Empty) is { Length: > 0 } failed) browser.Load(failed);
        else browser.Reload(ignoreCache);
    }

    // O endereço do site à vista no painel (o que falhou, quando está na página de erro).
    private static string WebPanelAddress(ChromiumWebBrowser? browser, string url)
        => browser?.Address is not { Length: > 0 } address ? url
            : ErrorPage.IsErrorPage(address) ? ErrorPage.FailedUrl(address) is { Length: > 0 } failed ? failed : url
            : address;

    private void OnWebPanelFullscreen(ChromiumWebBrowser browser)
    {
        if (browser.IsDisposed) return;
        PageScript.Run(browser, "document.fullscreenElement && document.exitFullscreen()");
        ShowToast("Ecrã inteiro só nas abas — botão direito › «Abrir este site numa aba»");
    }

    private void OnWebPanelCrashed(string url)
    {
        if (!_webPanelBrowsers.TryGetValue(url, out var browser) || browser.IsDisposed) return;
        var address = browser.Address is { Length: > 0 } current && !ErrorPage.IsErrorPage(current) ? current : url;
        browser.Load(ErrorPage.Url(ErrorPage.RendererCrashed, address));
    }

    private void ReleaseWebPanelBrowser(string url)
    {
        if (_webPanelInjectors.Remove(url, out var injector)) injector.Dispose();
        if (!_webPanelBrowsers.Remove(url, out var browser)) return;
        if (ReferenceEquals(_findPanel, browser)) CloseFind();
        SidebarWebHost.Children.Remove(browser);
        PageScript.Release(browser);
        browser.Dispose();
    }

    private void ReleaseWebPanelBrowsers()
    {
        foreach (var url in _webPanelBrowsers.Keys.ToList()) ReleaseWebPanelBrowser(url);
    }

    private ChromiumWebBrowser? CurrentWebPanelBrowser
        => _sidebarPanel?.Url is { } url && _webPanelBrowsers.TryGetValue(url, out var browser) && !browser.IsDisposed ? browser : null;

    private void WebPanelOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarPanel?.Url is not { } url) return;
        Tabs.NewTab(WebPanelAddress(CurrentWebPanelBrowser, url));
    }

    private void WebPanelReload_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentWebPanelBrowser is { } browser) ReloadWebPanel(browser, ignoreCache: false);
    }

    // Menu de contexto do painel: o menu escuro do Éteris, só com o que faz sentido dentro da barra.
    private void ShowWebPanelMenu(ChromiumWebBrowser browser, PageContext context)
    {
        var menu = new DarkMenu();
        var pendingSeparator = false;
        var any = false;
        foreach (var item in PageMenu.Build(context, Engine.Name))
        {
            if (item is null) { pendingSeparator = any; continue; }
            if (!WebPanelSupports(item.Command)) continue;
            if (pendingSeparator) menu.Separator();
            pendingSeparator = false;
            any = true;
            var command = item.Command;
            menu.Item(item.Glyph, item.Label, () => RunWebPanelCommand(command, browser, context), item.Gesture, item.Enabled);
        }
        if (_sidebarPanel?.Url is { } url)
        {
            if (any) menu.Separator();
            var mobile = WebPanels.Find(_webPanels, url)?.Mobile == true;
            menu.Item("", "Abrir este site numa aba", () => Tabs.NewTab(WebPanelAddress(browser, url)))
                .Item(mobile ? "" : "", "Vista móvel", () => SetPinnedSiteMobile(url, !mobile))
                .Item("", "Tirar da barra lateral", () => UnpinSite(url));
        }
        menu.Open(browser);
    }

    private static bool WebPanelSupports(PageCommand command) => command is PageCommand.Back or PageCommand.Forward or PageCommand.Reload
        or PageCommand.OpenLinkNewTab or PageCommand.CopyLink or PageCommand.OpenImageNewTab or PageCommand.CopyImageUrl
        or PageCommand.Undo or PageCommand.Cut or PageCommand.Copy or PageCommand.Paste or PageCommand.SelectAll
        or PageCommand.SearchSelection or PageCommand.Inspect;

    private void RunWebPanelCommand(PageCommand command, ChromiumWebBrowser browser, PageContext context)
    {
        var frame = browser.GetBrowser()?.FocusedFrame;
        switch (command)
        {
            case PageCommand.Back: browser.Back(); break;
            case PageCommand.Forward: browser.Forward(); break;
            case PageCommand.Reload: ReloadWebPanel(browser, ignoreCache: false); break;
            case PageCommand.Inspect: browser.GetBrowserHost()?.ShowDevTools(); break;
            case PageCommand.OpenLinkNewTab: Tabs.NewTab(context.LinkUrl!, activate: false); break;
            case PageCommand.CopyLink: CopyText(UrlCleaner.Clean(context.LinkUrl!), "Endereço do link copiado (sem rastreio)"); break;
            case PageCommand.OpenImageNewTab: Tabs.NewTab(context.ImageUrl!, activate: false); break;
            case PageCommand.CopyImageUrl: CopyText(context.ImageUrl!, "Endereço da imagem copiado"); break;
            case PageCommand.Undo: frame?.Undo(); break;
            case PageCommand.Cut: frame?.Cut(); break;
            case PageCommand.Copy: frame?.Copy(); break;
            case PageCommand.Paste: frame?.Paste(); break;
            case PageCommand.SelectAll: frame?.SelectAll(); break;
            case PageCommand.SearchSelection: Tabs.NewTab(Engine.UrlFor(context.Selection.Trim())); break;
        }
    }

    // Botão de um site na coluna dos separadores: o ícone guardado do site ou o monograma; botão direito dá o menu.
    private FrameworkElement WebPanelRailContent(WebPanel panel)
    {
        if (StoredSiteIcon(panel.Url) is { } icon)
            return new Image { Width = 18, Height = 18, Source = icon, SnapsToDevicePixels = true };
        var color = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(global::Eteris.Browser.Tabs.SiteBadge.Palette[global::Eteris.Browser.Tabs.SiteBadge.ColorIndex(panel.Url)])!;
        return new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Background = color,
            Child = new TextBlock
            {
                Text = global::Eteris.Browser.Tabs.SiteBadge.Letter(panel.Url) ?? "·", FontSize = 10.5, FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1),
                FontFamily = FontFamily,
            },
        };
    }

    private void ShowWebPanelRailMenu(WebPanel panel, UIElement anchor)
    {
        var index = WebPanels.IndexOf(_webPanels, panel.Url);
        new DarkMenu()
            .Item("", "Abrir numa aba", () => Tabs.NewTab(panel.Url))
            .Item("", "Recarregar", () =>
            {
                if (_webPanelBrowsers.TryGetValue(panel.Url, out var browser) && !browser.IsDisposed) ReloadWebPanel(browser, ignoreCache: false);
            }, enabled: _webPanelBrowsers.ContainsKey(panel.Url))
            .Item(panel.Mobile ? "" : "", "Vista móvel", () => SetPinnedSiteMobile(panel.Url, !panel.Mobile))
            .Separator()
            .Item("", "Mover para cima", () => MovePinnedSite(panel.Url, -1), enabled: index > 0)
            .Item("", "Mover para baixo", () => MovePinnedSite(panel.Url, 1), enabled: index >= 0 && index < _webPanels.Count - 1)
            .Separator()
            .Item("", "Tirar da barra lateral", () => UnpinSite(panel.Url))
            .OpenBeside(anchor, toLeft: SidebarSideNow == SidebarSide.Right);
    }
}
