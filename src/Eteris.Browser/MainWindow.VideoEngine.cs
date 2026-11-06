using System.IO;
using CefSharp;
using System.Windows;
using System.Windows.Input;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Eteris.Browser;

// Abas no motor de vídeo (WebView2, o motor do Edge que vem com o Windows): para os sites que só têm vídeo em
// H.264/AAC, formatos patenteados que o motor principal não traz. A aba continua a ser uma aba do Éteris — mesma
// barra, mesmo bloqueador, mesma sessão —; só o motor por baixo muda. Ver Tabs/VideoEngine.cs.
public partial class MainWindow
{
    private readonly Dictionary<BrowserTab, WebView2CompositionControl> _videoViews = new();

    // Um único ambiente do motor para todas as janelas (o WebView2 exige os mesmos argumentos por pasta de dados).
    // As janelas privadas usam o modo InPrivate do motor: nada fica gravado em disco.
    private static Task<CoreWebView2Environment>? s_videoEnvironment;

    private static Task<CoreWebView2Environment> VideoEnvironment()
        => s_videoEnvironment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(AppPaths.Root, "video-engine"),
            options: new CoreWebView2EnvironmentOptions(VideoEngine.BrowserArguments, language: "pt-PT"));

    private WebView2CompositionControl? ActiveVideo
        => Tabs.Active is { } tab && _videoViews.TryGetValue(tab, out var view) ? view : null;

    private static bool IsVideoSite(string? url) => VideoEngine.CanHost(url) && WebHost(url) is { } host && AppData.IsVideoSite(host);

    // Chamado por CreateBrowser: a aba já estava no motor de vídeo (sessão, a acordar) ou o site está lembrado.
    private bool TryCreateVideoView(BrowserTab tab)
    {
        if (!VideoEngine.CanHost(tab.Address)) tab.UsesVideoEngine = false;
        else if (IsVideoSite(tab.Address)) tab.UsesVideoEngine = true;
        if (!tab.UsesVideoEngine) return false;
        CreateVideoView(tab);
        return true;
    }

    private void CreateVideoView(BrowserTab tab)
    {
        var view = new WebView2CompositionControl
        {
            Visibility = Visibility.Hidden,
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
        };
        _videoViews[tab] = view;
        BrowserHost.Children.Add(view);
        WatchPaneFocus(tab, view);
        LayoutBrowsers();
        _ = InitializeVideoViewAsync(tab, view, tab.Address);
    }

    private async Task InitializeVideoViewAsync(BrowserTab tab, WebView2CompositionControl view, string url)
    {
        CoreWebView2Environment environment;
        try
        {
            environment = await VideoEnvironment();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = IsPrivate;
            options.ProfileName = IsPrivate ? "privado" : "eteris";
            await view.EnsureCoreWebView2Async(environment, options);
        }
        catch (Exception)
        {
            // Sem o runtime do WebView2 (Windows muito antigo ou removido à mão): a aba fica no motor principal.
            s_videoEnvironment = null;
            if (!_videoViews.TryGetValue(tab, out var current) || !ReferenceEquals(current, view)) return;
            ReleaseVideoView(tab);
            tab.UsesVideoEngine = false;
            if (Tabs.Tabs.Contains(tab) && !tab.IsSleeping) CreateBrowser(tab);
            ShowToast("O motor de vídeo do Windows (WebView2) não está disponível neste computador");
            return;
        }

        // A aba pode ter fechado ou adormecido enquanto o motor arrancava.
        if (!_videoViews.TryGetValue(tab, out var still) || !ReferenceEquals(still, view)) return;
        var core = view.CoreWebView2;
        ConfigureVideoPrivacy(core);
        WireVideoView(tab, view, core, environment);
        if (tab.IsMuted) core.IsMuted = true;
        core.Navigate(url);
    }

    private static void ConfigureVideoPrivacy(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsReputationCheckingRequired = false; // SmartScreen: mandaria os endereços à Microsoft
        settings.IsStatusBarEnabled = false;
        // Os atalhos são os do Éteris (Ctrl+T, Ctrl+L, F5…), não os do Edge.
        settings.AreBrowserAcceleratorKeysEnabled = false;
        try { core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict; }
        catch (Exception) { }
    }

    private void WireVideoView(BrowserTab tab, WebView2CompositionControl view, CoreWebView2 core, CoreWebView2Environment environment)
    {
        string? mainNavigation = null;

        // Bloqueador: os mesmos filtros do motor principal, em todos os pedidos (incluindo os dos workers).
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            var requestUrl = e.Request.Uri;
            if (string.Equals(requestUrl, mainNavigation, StringComparison.Ordinal)) return;
            var pageUrl = core.Source;
            if (WebHost(pageUrl) is not { } pageHost || !AppData.IsShieldOn(pageHost)) return;
            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)) return;
            var type = e.ResourceContext == CoreWebView2WebResourceContext.Document ? "sub_frame" : VideoEngine.ToAdblock(e.ResourceContext);
            if (!ShieldEngine.Current.ShouldBlock(requestUrl, pageUrl, type)) return;
            e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            ShieldStats.CountBlocked(persist: !IsPrivate);
            tab.RecordBlocked(request.Host);
        };

        core.NavigationStarting += (_, e) =>
        {
            // Páginas internas do Éteris só existem no motor principal.
            if (!VideoEngine.CanHost(e.Uri))
            {
                e.Cancel = true;
                if (!e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) Dispatcher.InvokeAsync(() => SwitchToNormalEngine(tab, e.Uri));
                return;
            }
            mainNavigation = e.Uri;
            if (!e.IsRedirected) tab.ResetShield();
            tab.IsLoading = true;
        };
        core.SourceChanged += (_, _) =>
        {
            var address = core.Source;
            if (!SameHost(tab.Address, address))
            {
                tab.Favicon = null;
                tab.Tint = null;
            }
            tab.IsReaderable = false;
            tab.Address = address;
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            var title = core.DocumentTitle;
            tab.Title = string.IsNullOrEmpty(title) ? tab.Address : title;
            var url = tab.Address;
            if (!IsPrivate && !string.IsNullOrEmpty(title)) AppData.TryWrite(h => h.UpdateTitle(url, title));
        };
        core.NavigationCompleted += (_, e) =>
        {
            tab.IsLoading = false;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
            var url = core.Source;
            if (e.IsSuccess && e.HttpStatusCode is > 0 and < 400 && !IsPrivate)
                AppData.TryWrite(h => h.Record(url, tab.Title, DateTime.UtcNow));
        };
        core.HistoryChanged += (_, _) =>
        {
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
        };
        core.FaviconChanged += (_, _) => SetFavicon(tab, core.FaviconUri);
        core.ContainsFullScreenElementChanged += (_, _) => OnPageFullscreen(tab, core.ContainsFullScreenElement);
        core.IsDocumentPlayingAudioChanged += (_, _) => tab.IsAudible = core.IsDocumentPlayingAudio;
        core.StatusBarTextChanged += (_, _) => ShowLinkStatus(tab, Eteris.Browser.Omnibox.LinkStatus.Format(core.StatusBarText));
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                Dispatcher.InvokeAsync(() => OnPageCrashed(tab));
        };
        // Janelas novas abrem como abas do Éteris, com a mesma regra dos pop-ups do motor principal.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var target = e.Uri;
            if (PopupPolicy.Allow(e.IsUserInitiated, target, core.Source, ShieldEngine.Current, AppData.IsShieldOn))
                Tabs.NewTab(target);
            else if (WebHost(target) is { } blocked)
            {
                ShieldStats.CountBlocked(persist: !IsPrivate);
                tab.RecordBlocked(blocked);
            }
        };
    }

    // O motor principal avisou que o vídeo desta página só existe em formatos patenteados.
    private void OnPatentedVideoReport(BrowserTab tab, string pageUrl)
    {
        if (!_browsers.ContainsKey(tab) || !VideoEngine.CanHost(pageUrl) || WebHost(pageUrl) is not { } host) return;
        // Numa janela privada o site não é lembrado: a lista ficaria em disco depois de a janela fechar.
        if (!IsPrivate) AppData.SetVideoSite(host, true);
        SwitchToVideoEngine(tab, pageUrl);
        ShowToast($"O vídeo de {TrackerBlocker.RegistrableDomain(host)} usa H.264 — esta aba passou para o motor de vídeo");
    }

    private void SwitchToVideoEngine(BrowserTab tab, string url)
    {
        if (!Tabs.Tabs.Contains(tab) || _videoViews.ContainsKey(tab) || !VideoEngine.CanHost(url)) return;
        ReleaseChromium(tab);
        tab.Address = url;
        tab.UsesVideoEngine = true;
        if (!tab.IsSleeping) CreateVideoView(tab);
    }

    private void SwitchToNormalEngine(BrowserTab tab, string url)
    {
        if (!Tabs.Tabs.Contains(tab) || !_videoViews.ContainsKey(tab)) return;
        ReleaseVideoView(tab);
        tab.UsesVideoEngine = false;
        tab.Address = url;
        if (!tab.IsSleeping) CreateBrowser(tab);
        LayoutBrowsers();
    }

    // Botão «Motor de vídeo» na barra de endereço: o site deixa de ser lembrado e a aba volta ao motor principal.
    private void VideoEngineBadge_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab || !tab.UsesVideoEngine) return;
        if (WebHost(tab.Address) is { } host) AppData.SetVideoSite(host, false);
        SwitchToNormalEngine(tab, tab.Address);
        ShowToast("Este site volta a abrir no motor principal");
    }

    private void UpdateVideoEngineBadge()
        => VideoEngineBadge.Visibility = Tabs.Active?.UsesVideoEngine == true ? Visibility.Visible : Visibility.Collapsed;

    private void ReleaseVideoView(BrowserTab tab)
    {
        if (!_videoViews.Remove(tab, out var view)) return;
        BrowserHost.Children.Remove(view);
        view.Dispose();
    }

    private void DisposeVideoViews()
    {
        foreach (var view in _videoViews.Values) view.Dispose();
        _videoViews.Clear();
    }

    // ---------- Navegação comum aos dois motores ----------

    private void GoBack()
    {
        if (ActiveVideo?.CoreWebView2 is { } core) { if (core.CanGoBack) core.GoBack(); }
        else ActiveBrowser?.Back();
    }

    private void GoForward()
    {
        if (ActiveVideo?.CoreWebView2 is { } core) { if (core.CanGoForward) core.GoForward(); }
        else ActiveBrowser?.Forward();
    }

    private void StopActive()
    {
        if (ActiveVideo?.CoreWebView2 is { } core) core.Stop();
        else ActiveBrowser?.Stop();
    }

    private bool ReloadVideo()
    {
        if (ActiveVideo is not { } view) return false;
        view.CoreWebView2?.Reload();
        return true;
    }

    // Abrir um endereço numa aba do motor de vídeo: se o destino não é um site de vídeo, a aba volta ao motor principal.
    private bool LoadInVideoView(BrowserTab tab, string url)
    {
        if (!_videoViews.TryGetValue(tab, out var view)) return false;
        if (!VideoEngine.CanHost(url) || !(IsVideoSite(url) || SameSite(tab.Address, url)))
        {
            SwitchToNormalEngine(tab, url);
            return true;
        }
        tab.Address = url;
        if (view.CoreWebView2 is { } core) core.Navigate(url);
        Keyboard.Focus(view);
        return true;
    }

    private static bool SameSite(string a, string b)
        => WebHost(a) is { } ha && WebHost(b) is { } hb
           && TrackerBlocker.RegistrableDomain(ha.ToLowerInvariant()) == TrackerBlocker.RegistrableDomain(hb.ToLowerInvariant());

    private void SetVideoMuted(BrowserTab tab)
    {
        if (_videoViews.TryGetValue(tab, out var view) && view.CoreWebView2 is { } core) core.IsMuted = tab.IsMuted;
    }
}
