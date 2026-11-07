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

    private static WebPanelStore WebPanelsFile => new(Path.Combine(AppPaths.Root, "paineis.json"));

    private void LoadWebPanels()
    {
        _webPanels = IsPrivate ? [] : WebPanelsFile.Load();
        Closed += (_, _) => ReleaseWebPanelBrowsers();
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
        if (WebPanels.Add(_webPanels, tab.Address, tab.Title) is not { } list)
        {
            ShowToast(IsPinnedSite(tab.Address) ? "Este site já está na barra lateral" : $"A barra lateral já tem {WebPanels.Max} sites fixados");
            return;
        }
        SaveWebPanels(list);
        OpenSidebar(SidebarPanels.WebKey(WebPanels.Normalize(tab.Address)!));
        ShowToast($"«{SidebarLists.HostOf(tab.Address)}» fixado na barra lateral");
    }

    private void UnpinSite(string url)
    {
        ReleaseWebPanelBrowser(url);
        SaveWebPanels(WebPanels.Remove(_webPanels, url));
        if (_sidebarPanel?.Url == url)
        {
            if (_sidebarOpen && SidebarAvailable.Count > 0) ShowSidebarPanel(SidebarAvailable[0], focus: false);
            else CloseSidebar();
        }
        ShowToast("Site tirado da barra lateral");
    }

    private void MovePinnedSite(string url, int delta) => SaveWebPanels(WebPanels.Move(_webPanels, url, delta));

    // «Vista móvel»: o motor nasce de novo com o agente de telemóvel (ou sem ele) e o site volta a carregar.
    private void SetPinnedSiteMobile(string url, bool mobile)
    {
        ReleaseWebPanelBrowser(url);
        SaveWebPanels(WebPanels.SetMobile(_webPanels, url, mobile));
        if (_sidebarOpen && _sidebarPanel?.Url == url) ShowWebPanel(_sidebarPanel, focus: false);
        ShowToast(mobile ? "Vista móvel ligada — o site vê um telemóvel" : "Vista móvel desligada");
    }

    private void SaveWebPanels(IReadOnlyList<WebPanel> list)
    {
        _webPanels = list;
        try { WebPanelsFile.Save(list); }
        catch (Exception) { ShowToast("Não foi possível guardar os sites fixados"); }
        BuildSidebarRail();
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
        var browser = new ChromiumWebBrowser(url)
        {
            Visibility = Visibility.Hidden,
            // Os atalhos do Éteris chegam do painel como de qualquer página; pop-ups viram abas (só com gesto).
            KeyboardHandler = new ShortcutKeyboardHandler(ShortcutFromPage, KeyUpFromPage),
            LifeSpanHandler = new PopupToTabHandler(request =>
            {
                if (request.UserGesture && PageMenu.IsWebLink(request.TargetUrl))
                    Dispatcher.InvokeAsync(() => Tabs.NewTab(request.TargetUrl, activate: request.Foreground));
            }),
            DownloadHandler = DownloadHandler,
            PermissionHandler = PermissionHandler,
            RequestHandler = new PrivacyRequestHandler(() => ShieldEngine.Current, AppData.IsShieldOn,
                onNavigate: (_, _) => Task.CompletedTask,
                onBlocked: _ => ShieldStats.CountBlocked(persist: true),
                // Vista móvel: os pedidos deste motor saem com o agente de um telemóvel.
                userAgent: WebPanels.Find(_webPanels, url)?.Mobile == true ? WebPanels.MobileUserAgent : null),
        };
        browser.MenuHandler = new PageMenuHandler(IsPrivate, (context, _, _) => Dispatcher.InvokeAsync(() => ShowWebPanelMenu(browser, context)));
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

    private void ReleaseWebPanelBrowser(string url)
    {
        if (!_webPanelBrowsers.Remove(url, out var browser)) return;
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
        Tabs.NewTab(CurrentWebPanelBrowser?.Address is { Length: > 0 } address ? address : url);
    }

    private void WebPanelReload_Click(object sender, RoutedEventArgs e) => CurrentWebPanelBrowser?.Reload();

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
            menu.Item("", "Abrir este site numa aba", () => Tabs.NewTab(browser.Address ?? url))
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
            case PageCommand.Reload: browser.Reload(); break;
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
                if (_webPanelBrowsers.TryGetValue(panel.Url, out var browser) && !browser.IsDisposed) browser.Reload();
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
