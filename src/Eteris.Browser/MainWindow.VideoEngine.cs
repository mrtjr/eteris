using System.IO;
using System.Text.Json;
using CefSharp;
using System.Windows;
using System.Windows.Input;
using Eteris.Browser.Browser;
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
//
// Usa o controlo WebView2 (janela nativa) e não o WebView2CompositionControl: este último precisa da projeção do
// Windows SDK (Microsoft.Windows.SDK.NET.dll), cuja licença exige impor termos aos utilizadores e proíbe licenças
// que obriguem a abrir o código — incompatível com a GPL do Éteris. Consequência: os avisos desenhados por cima da
// página (aviso curto, paleta) não aparecem por cima da área do vídeo; os menus e sugestões (janelas próprias) sim.
public partial class MainWindow
{
    private readonly Dictionary<BrowserTab, WebView2> _videoViews = new();

    // Abas a meio da troca para o motor de vídeo: a página do motor principal continua à vista (pausada) até o
    // WebView2 ter o primeiro documento, e só então troca — sem o intervalo vazio de antes (V3).
    private readonly HashSet<BrowserTab> _videoSwitching = new();

    // Janela privada: os sites que o utilizador mandou voltar ao motor principal ficam só em memória.
    private readonly HashSet<string> _privateVideoOptOut = new(StringComparer.OrdinalIgnoreCase);

    // Um único ambiente do motor para todas as janelas (o WebView2 exige os mesmos argumentos por pasta de dados).
    // As janelas privadas usam o modo InPrivate do motor: nada fica gravado em disco.
    private static Task<CoreWebView2Environment>? s_videoEnvironment;

    private static Task<CoreWebView2Environment> VideoEnvironment()
        => s_videoEnvironment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(AppPaths.Root, "video-engine"),
            options: new CoreWebView2EnvironmentOptions(VideoEngine.BrowserArguments(Settings.Current.BlockAutoplay), language: "pt-PT"));

    private WebView2? ActiveVideo
        => Tabs.Active is { } tab && _videoViews.TryGetValue(tab, out var view) ? view : null;

    private bool IsVideoOptOut(string host)
        => IsPrivate ? _privateVideoOptOut.Contains(TrackerBlocker.RegistrableDomain(host.ToLowerInvariant())) || AppData.IsVideoOptOut(host)
            : AppData.IsVideoOptOut(host);

    // Abre no motor de vídeo: site lembrado, ou da lista embutida de sites só em H.264, e não excluído pelo utilizador.
    private bool IsVideoSite(string? url)
        => VideoEngine.CanHost(url) && WebHost(url) is { } host
           && VideoEngine.OpensInVideoEngine(TrackerBlocker.RegistrableDomain(host.ToLowerInvariant()), AppData.IsVideoSite(host), IsVideoOptOut(host));

    // Chamado por CreateBrowser: a aba já estava no motor de vídeo (sessão, a acordar) ou o site está lembrado.
    private bool TryCreateVideoView(BrowserTab tab)
    {
        if (!VideoEngine.CanHost(tab.Address)) tab.UsesVideoEngine = false;
        else if (IsVideoSite(tab.Address)) tab.UsesVideoEngine = true;
        if (!tab.UsesVideoEngine) return false;
        CreateVideoView(tab);
        return true;
    }

    private void CreateVideoView(BrowserTab tab, double resumeAt = 0)
    {
        var view = new WebView2
        {
            Visibility = Visibility.Hidden,
            // Opaco: enquanto o primeiro documento não pinta, vê-se branco (como a página web), não o Mica.
            DefaultBackgroundColor = System.Drawing.Color.White,
        };
        _videoViews[tab] = view;
        BrowserHost.Children.Add(view);
        WatchPaneFocus(tab, view);
        LayoutBrowsers();
        _ = InitializeVideoViewAsync(tab, view, tab.Address, resumeAt);
    }

    private async Task InitializeVideoViewAsync(BrowserTab tab, WebView2 view, string url, double resumeAt)
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
            var wasSwitching = _videoSwitching.Remove(tab);
            ReleaseVideoView(tab);
            tab.UsesVideoEngine = false;
            if (Tabs.Tabs.Contains(tab) && !tab.IsSleeping && !(wasSwitching && _browsers.ContainsKey(tab))) CreateBrowser(tab);
            LayoutBrowsers();
            ShowToast("O motor de vídeo do Windows (WebView2) não está disponível neste computador");
            return;
        }

        // A aba pode ter fechado ou adormecido enquanto o motor arrancava.
        if (!_videoViews.TryGetValue(tab, out var still) || !ReferenceEquals(still, view)) return;
        var core = view.CoreWebView2;
        ConfigureVideoPrivacy(core);
        WireVideoView(tab, view, core, environment);
        if (tab.IsMuted || TestSwitches.Mute) core.IsMuted = true;
        if (resumeAt > 1) await ResumeAtAsync(core, resumeAt);
        await EnsureVideoCosmeticsAsync(core, url, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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

    private const int ShieldCacheLimit = 4096;

    private void WireVideoView(BrowserTab tab, WebView2 view, CoreWebView2 core, CoreWebView2Environment environment)
    {
        string? mainNavigation = null;
        // O endereço da página, guardado na navegação: ler core.Source (COM) a cada pedido custava tempo na thread da
        // janela. As decisões do bloqueador repetem-se (o mesmo script em cada página do site): ficam em cache.
        var pageUrl = core.Source;
        var pageHost = WebHost(pageUrl);
        var decisions = new Dictionary<(string Page, string Request), bool>();
        var cosmeticHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bloqueador: os mesmos filtros do motor principal, em todos os pedidos (incluindo os dos workers), menos os
        // segmentos de vídeo/áudio, que saem logo. (Medido: um filtro por contexto, sem Media, deixava de bloquear.)
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            var context = e.ResourceContext;
            if (!VideoEngine.IsFiltered(context) || pageHost is null || !AppData.IsShieldOn(pageHost)) return;
            var requestUrl = e.Request.Uri;
            if (string.Equals(requestUrl, mainNavigation, StringComparison.Ordinal)) return;
            var type = context == CoreWebView2WebResourceContext.Document ? "sub_frame" : VideoEngine.ToAdblock(context);
            var key = (pageHost, type + " " + requestUrl);
            if (!decisions.TryGetValue(key, out var block))
            {
                if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri? _)) return;
                block = ShieldEngine.Current.ShouldBlock(requestUrl, pageUrl!, type);
                if (decisions.Count >= ShieldCacheLimit) decisions.Clear();
                decisions[key] = block;
            }
            if (!block) return;
            e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            ShieldStats.CountBlocked(persist: !IsPrivate);
            if (Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)) tab.RecordBlocked(request.Host);
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
            pageUrl = e.Uri;
            pageHost = WebHost(e.Uri);
            if (!e.IsRedirected) tab.ResetShield();
            tab.IsLoading = true;
            // A cosmética do Escudo deste site (registada uma vez por site, protegida pelo hostname da página).
            _ = EnsureVideoCosmeticsAsync(core, e.Uri, cosmeticHosts);
        };
        core.SourceChanged += (_, _) =>
        {
            var address = core.Source;
            pageUrl = address;
            pageHost = WebHost(address);
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
            if (!IsPrivate && !string.IsNullOrEmpty(title)) AppData.UpdateTitleLater(url, title);
        };
        core.NavigationCompleted += (_, e) =>
        {
            FinishVideoSwitch(tab);
            tab.IsLoading = false;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
            var url = core.Source;
            if (e.IsSuccess && e.HttpStatusCode is > 0 and < 400 && !IsPrivate)
            {
                var title = tab.Title;
                AppData.QueueWrite(h => h.Record(url, title, DateTime.UtcNow));
                RememberFavicon(tab, url);
            }
        };
        core.HistoryChanged += (_, _) =>
        {
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
        };
        core.FaviconChanged += async (_, _) => await SetVideoFaviconAsync(tab, core);
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

    // Regras cosméticas do Escudo no motor de vídeo (banners, pop-ups): as mesmas do motor principal, registadas pelo
    // DevTools para correr antes dos scripts da página, uma vez por site e protegidas pelo hostname da página de topo.
    private static async Task EnsureVideoCosmeticsAsync(CoreWebView2 core, string url, HashSet<string> done)
    {
        if (HostScriptRegistry.HostOf(url) is not { } page || !done.Add(page.IdnHost)) return;
        var css = SiteCosmeticScript(url);
        if (HostScriptRegistry.Guard(page.IdnHost, css.Length == 0 ? null : css) is not { } script) return;
        await DevToolsQuietly(core, "Page.addScriptToEvaluateOnNewDocument", new { source = script });
    }

    // Retomar o vídeo onde estava no motor principal: um script de uso único, no primeiro «loadedmetadata».
    private static Task ResumeAtAsync(CoreWebView2 core, double seconds)
    {
        var at = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var script = "(() => { const at = " + at + "; let done = false;"
            + " addEventListener('loadedmetadata', e => { const v = e.target;"
            + " if (done || !(v instanceof HTMLMediaElement) || !(v.duration > at)) return; done = true; v.currentTime = at; }, true); })();";
        return DevToolsQuietly(core, "Page.addScriptToEvaluateOnNewDocument", new { source = script });
    }

    private static async Task DevToolsQuietly(CoreWebView2 core, string method, object parameters)
    {
        try { await core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters)); }
        catch (Exception) { }
    }

    // O motor principal avisou que o vídeo desta página não toca nele (Tabs/VideoEngine.cs, VideoReport).
    private void OnVideoReport(BrowserTab tab, string pageUrl, VideoEngine.VideoReport report)
    {
        if (!_browsers.ContainsKey(tab) || _videoViews.ContainsKey(tab) || !VideoEngine.CanHost(pageUrl)
            || WebHost(pageUrl) is not { } host || !string.Equals(tab.Address, pageUrl, StringComparison.Ordinal)) return;
        switch (VideoEngine.Decide(report, IsVideoOptOut(host), TrackerBlocker.RegistrableDomain(host.ToLowerInvariant())))
        {
            case VideoEngine.Decision.Switch:
                // Numa janela privada o site não é lembrado: a lista ficaria em disco depois de a janela fechar.
                if (!IsPrivate) AppData.SetVideoSite(host, true);
                SwitchToVideoEngine(tab, pageUrl);
                ShowToast($"O vídeo de {TrackerBlocker.RegistrableDomain(host)} usa H.264 — esta aba passou para o motor de vídeo");
                break;
            case VideoEngine.Decision.Offer:
                if (tab.VideoOffer is not null) return;
                tab.VideoOffer = report.Reason;
                if (!ReferenceEquals(tab, Tabs.Active)) return;
                if (report.Reason == "drm")
                    ShowToast("Este site usa DRM (conteúdo protegido) — para o ver, «Tocar no motor de vídeo» na barra de endereço");
                else if (report.Reason == "frame-player" || report.Strong)
                    ShowToast("Este vídeo não toca no motor principal — «Tocar no motor de vídeo» na barra de endereço");
                break;
        }
    }

    // Botão «Tocar no motor de vídeo» (barra de endereço e menu da página): troca já e lembra o site.
    private void PlayInVideoEngine(BrowserTab tab)
    {
        if (!VideoEngine.CanHost(tab.Address) || WebHost(tab.Address) is not { } host) return;
        if (IsPrivate) _privateVideoOptOut.Remove(TrackerBlocker.RegistrableDomain(host.ToLowerInvariant()));
        else
        {
            AppData.SetVideoOptOut(host, false);
            AppData.SetVideoSite(host, true);
        }
        tab.VideoOffer = null;
        SwitchToVideoEngine(tab, tab.Address);
    }

    private void VideoOfferButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is { } tab) PlayInVideoEngine(tab);
    }

    private void SwitchToVideoEngine(BrowserTab tab, string url)
    {
        if (!Tabs.Tabs.Contains(tab) || _videoViews.ContainsKey(tab) || !VideoEngine.CanHost(url)) return;
        tab.Address = url;
        tab.UsesVideoEngine = true;
        tab.VideoOffer = null;
        if (tab.IsSleeping)
        {
            ReleaseChromium(tab);
            return;
        }
        if (!_browsers.TryGetValue(tab, out var browser))
        {
            CreateVideoView(tab);
            return;
        }
        // A página do motor principal fica à vista, calada, até o motor de vídeo ter o primeiro documento.
        _videoSwitching.Add(tab);
        _ = SwitchWhenReadyAsync(tab, browser, url);
    }

    private async Task SwitchWhenReadyAsync(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string url)
    {
        // Posição do maior vídeo (só conta se já estava a tocar há mais de 1 s); e calar a página antes de trocar.
        var position = await PageScript.EvaluateAsync(browser, VideoEngine.PositionScript, TimeSpan.FromMilliseconds(300));
        PageScript.Run(browser, MediaInfo.PauseScript);
        var resumeAt = position.Success && position.Result is double seconds ? seconds : 0;
        if (!_videoSwitching.Contains(tab) || !Tabs.Tabs.Contains(tab)) return;
        if (resumeAt > 1 && VideoEngine.IsDirectMedia(url)) url = VideoEngine.WithTimeFragment(url, resumeAt);
        tab.Address = url;
        CreateVideoView(tab, resumeAt);
        // Recurso: se o motor de vídeo não pintar em 5 s, a troca acaba na mesma (fica o branco do fundo).
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(5000);
            FinishVideoSwitch(tab);
        });
    }

    // O primeiro documento do motor de vídeo carregou: o WebView2 passa a ver-se e só depois (200 ms, já pintado) sai a
    // página do motor principal. Medido (docs/motor.md, V3): mostrar no DOMContentLoaded deixava ~220–250 ms de branco
    // antes da primeira pintura, e tirar o motor principal no mesmo instante dava às vezes um fotograma preto.
    private async void FinishVideoSwitch(BrowserTab tab)
    {
        if (!_videoSwitching.Remove(tab)) return;
        LayoutBrowsers();
        if (ReferenceEquals(tab, Tabs.Active) && _videoViews.TryGetValue(tab, out var view)) Keyboard.Focus(view);
        await Task.Delay(200);
        if (_videoViews.ContainsKey(tab) && !_videoSwitching.Contains(tab)) ReleaseChromium(tab);
        LayoutBrowsers();
    }

    private void SwitchToNormalEngine(BrowserTab tab, string url)
    {
        if (!Tabs.Tabs.Contains(tab) || !_videoViews.ContainsKey(tab)) return;
        _videoSwitching.Remove(tab);
        ReleaseVideoView(tab);
        tab.UsesVideoEngine = false;
        tab.Address = url;
        if (!tab.IsSleeping && !_browsers.ContainsKey(tab)) CreateBrowser(tab);
        LayoutBrowsers();
    }

    // Botão «Motor de vídeo» na barra de endereço: o site deixa de abrir no motor de vídeo (também os da lista
    // embutida: fica uma exclusão explícita) e a aba volta ao motor principal.
    private void VideoEngineBadge_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab || !tab.UsesVideoEngine) return;
        if (WebHost(tab.Address) is { } host)
        {
            AppData.SetVideoSite(host, false);
            if (IsPrivate) _privateVideoOptOut.Add(TrackerBlocker.RegistrableDomain(host.ToLowerInvariant()));
            else AppData.SetVideoOptOut(host, true);
        }
        SwitchToNormalEngine(tab, tab.Address);
        ShowToast("Este site volta a abrir no motor principal");
    }

    private void UpdateVideoEngineBadge()
    {
        var tab = Tabs.Active;
        VideoEngineBadge.Visibility = tab?.UsesVideoEngine == true ? Visibility.Visible : Visibility.Collapsed;
        VideoOfferButton.Visibility = tab is { UsesVideoEngine: false, VideoOffer: not null } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReleaseVideoView(BrowserTab tab)
    {
        _videoSwitching.Remove(tab);
        if (!_videoViews.Remove(tab, out var view)) return;
        BrowserHost.Children.Remove(view);
        view.Dispose();
    }

    private void DisposeVideoViews()
    {
        foreach (var view in _videoViews.Values) view.Dispose();
        _videoViews.Clear();
        _videoSwitching.Clear();
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
        if (_videoViews.TryGetValue(tab, out var view) && view.CoreWebView2 is { } core) core.IsMuted = tab.IsMuted || TestSwitches.Mute;
    }
}
