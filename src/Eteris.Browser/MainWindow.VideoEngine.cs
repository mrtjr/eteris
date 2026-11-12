using System.IO;
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
// barra, mesmo bloqueador, mesmo pedido de privacidade —; só o motor por baixo muda (e o perfil: é outro, com a sua pasta). Ver Tabs/VideoEngine.cs. Com o motor com codecs
// da plataforma (Tabs/PlatformCodecs.cs) o motor principal toca-os e isto fica só para quando ele responde «não».
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
            userDataFolder: AppPaths.VideoEngine,
            options: new CoreWebView2EnvironmentOptions(VideoEngine.BrowserArguments(Settings.Current.BlockAutoplay),
                language: VideoEngine.Language(EngineHardening.StartedWithLanguage)) { IsCustomCrashReportingEnabled = true });

    private WebView2? ActiveVideo
        => Tabs.Active is { } tab && _videoViews.TryGetValue(tab, out var view) ? view : null;

    private bool IsVideoOptOut(string host)
        => IsPrivate ? _privateVideoOptOut.Contains(TrackerBlocker.RegistrableDomain(host.ToLowerInvariant())) || AppData.IsVideoOptOut(host)
            : AppData.IsVideoOptOut(host);

    // Abre no motor de vídeo: site lembrado, ou da lista embutida de sites só em H.264 (sem codecs no motor principal),
    // e não excluído pelo utilizador.
    private bool IsVideoSite(string? url)
    {
        if (!VideoEngine.CanHost(url) || WebHost(url) is not { } host) return false;
        var codecs = PlatformCodecs.MainHasCodecs;
        return VideoEngine.OpensInVideoEngine(TrackerBlocker.RegistrableDomain(host.ToLowerInvariant()), AppData.IsVideoSite(host, codecs),
            IsVideoOptOut(host), codecs);
    }

    // Sonda única por sessão (Tabs/PlatformCodecs.cs): só com a compilação própria do motor, na 1.ª página interna que
    // acabar de carregar. Se o motor disser «não» ao H.264/AAC, tudo volta a ser como no motor sem codecs.
    // Nunca durante uma troca de páginas: a 1.ª pergunta ao motor de média da sessão atrasa a apresentação no ecrã e,
    // feita no fim do carregamento da 1.ª aba nova, a página antiga saía antes de a nova aparecer (PlatformCodecs.ProbeDelay).
    private async Task ProbePlatformCodecsAsync(CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string url)
    {
        if (!PlatformCodecs.TryStartProbe(url)) return;
        await Task.Delay(PlatformCodecs.ProbeDelay);
        for (var wait = TimeSpan.Zero; IsSwappingPages && wait < PageSwap.HoldLimit; wait += PlatformCodecs.ProbeRetry)
            await Task.Delay(PlatformCodecs.ProbeRetry);
        var answer = await PageScript.EvaluateAsync(browser, PlatformCodecs.ProbeScript, TimeSpan.FromSeconds(2));
        PlatformCodecs.Record(answer.Success, answer.Result);
    }

    // Chamado por CreateBrowser: a aba já estava no motor de vídeo (sessão, a acordar) ou o site está lembrado.
    private bool TryCreateVideoView(BrowserTab tab)
    {
        if (!VideoEngine.CanHost(tab.Address)) tab.UsesVideoEngine = false;
        // A página a que se regressou com o Voltar (Tabs/EngineCrossing.cs) fica no motor principal.
        else if (IsVideoSite(tab.Address) && !tab.Crossing.StaysInMain(tab.Address)) tab.UsesVideoEngine = true;
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
        // Os sites cuja cosmética já está registada nesta aba: o mesmo conjunto aqui e nas navegações (WireVideoView).
        var cosmeticHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WireVideoView(tab, view, core, environment, cosmeticHosts);
        if (tab.IsMuted || TestSwitches.Mute) core.IsMuted = true;
        if (resumeAt > 1) await ResumeAtAsync(core, resumeAt);
        await EnsureVideoCosmeticsAsync(core, url, cosmeticHosts);
        // O GPC visível ao JavaScript e a lista de dispositivos sem nomes, antes dos scripts da página (o cabeçalho
        // Sec-GPC vai em WireVideoView).
        await VideoPageScript.AddEarlyAsync(core, PrivacySignals.SessionScript);
        core.Navigate(VideoUrl(url));
    }

    // O WebView2 não passa pelo tratador de pedidos do Chromium: o «HTTPS primeiro» aplica-se aqui antes de navegar
    // (sem o aviso — um site de vídeo lembrado já abriu por https:// no motor principal).
    private string VideoUrl(string url)
        => Data.Settings.Current.HttpsFirst && ShieldEngine.Https.Secure(url, IsPrivate) is { } secure ? secure : url;

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

    private void WireVideoView(BrowserTab tab, WebView2 view, CoreWebView2 core, CoreWebView2Environment environment, HashSet<string> cosmeticHosts)
    {
        string? mainNavigation = null;
        // O endereço da página, guardado na navegação: ler core.Source (COM) a cada pedido custava tempo na thread da
        // janela. As decisões do bloqueador repetem-se (o mesmo script em cada página do site): ficam em cache.
        var pageUrl = core.Source;
        var pageHost = WebHost(pageUrl);
        var decisions = new Dictionary<(string Page, string Request), bool>();
        // O documento atual já deu um título? Um ficheiro de vídeo aberto diretamente não dá (nem o DocumentTitleChanged
        // dispara): sem isto ficava o título da página anterior, na aba e no histórico.
        var titled = false;

        // Bloqueador: os mesmos filtros do motor principal, em todos os pedidos (incluindo os dos workers), menos os
        // segmentos de vídeo/áudio, que saem logo. (Medido: um filtro por contexto, sem Media, deixava de bloquear.)
        // Ficheiros de média que o WebView2 gravava em vez de tocar (VideoEngine.PlaysInsteadOfDownload): a transferência
        // é cancelada (nada fica no disco) e o mesmo endereço volta a abrir, servido como uma página com o leitor.
        var players = new Dictionary<string, string?>(StringComparer.Ordinal);
        core.DownloadStarting += (_, e) =>
        {
            var download = e.DownloadOperation;
            var target = VideoEngine.WithoutFragment(download.Uri);
            if (players.ContainsKey(target))
            {
                e.Cancel = true;
                return;
            }
            if (!VideoEngine.PlaysInsteadOfDownload(download.Uri, mainNavigation, download.MimeType, download.ContentDisposition)) return;
            e.Cancel = true;
            players[target] = download.MimeType;
            Dispatcher.InvokeAsync(() => core.Navigate(target));
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            // O pedido de privacidade (GPC) sai em todos os pedidos, também nos que o bloqueador deixa passar.
            foreach (var (name, value) in PrivacySignals.Headers) e.Request.Headers.SetHeader(name, value);
            var context = e.ResourceContext;
            if (context == CoreWebView2WebResourceContext.Document && players.Count > 0
                && players.TryGetValue(VideoEngine.WithoutFragment(e.Request.Uri), out var playerMime))
            {
                var page = System.Text.Encoding.UTF8.GetBytes(VideoEngine.PlayerPage(e.Request.Uri, playerMime));
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(page), 200, "OK", "Content-Type: text/html; charset=utf-8");
                return;
            }
            if (!VideoEngine.IsFiltered(context) || pageHost is null || !ShieldOn(pageHost)) return;
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

        // Notificações: recusadas, como no motor principal (o WebView2 perguntaria com uma caixa do Edge).
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications) e.State = CoreWebView2PermissionState.Deny;
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
            NoteVideoForgetPending(e.Uri);
            mainNavigation = e.Uri;
            pageUrl = e.Uri;
            pageHost = WebHost(e.Uri);
            if (!e.IsRedirected)
            {
                tab.ResetShield();
                titled = false;
            }
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
            // Na troca para o motor de vídeo o endereço da aba já é o novo: o ícone do site anterior sai pelo site dele.
            else tab.ForgetFaviconOfOtherSite(address);
            tab.IsReaderable = false;
            tab.Address = address;
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            var title = core.DocumentTitle;
            var url = tab.Address;
            tab.Title = VideoEngine.PageTitle(title, url);
            if (string.IsNullOrEmpty(title)) return;
            titled = true;
            if (!IsPrivate) AppData.UpdateTitleLater(url, title);
        };
        // A navegação chegou a ter documento (e não é uma página de erro): um ficheiro de média aberto diretamente acaba
        // «abortado» mas conta como visita (Data/HistoryVisit.cs).
        ulong? committedNavigation = null;
        core.ContentLoading += (_, e) => committedNavigation = e.IsErrorPage ? null : e.NavigationId;
        core.NavigationCompleted += (_, e) =>
        {
            FinishVideoSwitch(tab);
            tab.IsLoading = false;
            UpdateVideoHistory(tab, core);
            var url = core.Source;
            // Sem título do documento (ficheiro de vídeo aberto diretamente): o nome do ficheiro, como no Chrome.
            if (!titled) tab.Title = VideoEngine.PageTitle(core.DocumentTitle, url);
            var aborted = e.WebErrorStatus is CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.OperationCanceled;
            if (HistoryVisit.FromVideoEngine(e.IsSuccess, e.HttpStatusCode, aborted, committedNavigation == e.NavigationId) && !IsPrivate)
            {
                var title = tab.Title;
                AppData.QueueWrite(h => h.Record(url, title, DateTime.UtcNow));
                RememberFavicon(tab, url);
            }
        };
        core.HistoryChanged += (_, _) => UpdateVideoHistory(tab, core);
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
            if (PopupPolicy.Allow(e.IsUserInitiated, target, core.Source, ShieldEngine.Current, ShieldOn))
                Tabs.NewTab(target);
            else if (WebHost(target) is { } blocked)
            {
                ShieldStats.CountBlocked(persist: !IsPrivate);
                tab.RecordBlocked(blocked);
            }
        };
    }

    // Um site com «Esquecer ao fechar» abriu numa aba do motor de vídeo de uma janela normal: fica pendente para a
    // varredura do arranque (AppData.VideoForgetPending), caso a limpeza à saída não chegue a acontecer.
    private void NoteVideoForgetPending(string url)
    {
        if (IsPrivate || SiteScope.SiteOf(url) is not { } site || !AppData.IsForgetOnClose(site)) return;
        AppData.VideoForgetPending.Add(site);
    }

    // Voltar/Avançar de uma aba do motor de vídeo: o histórico dele e, na 1.ª entrada, o regresso ao motor principal.
    private static void UpdateVideoHistory(BrowserTab tab, CoreWebView2 core)
    {
        tab.CanGoBack = tab.Crossing.CanGoBack(inVideo: true, core.CanGoBack);
        tab.CanGoForward = tab.Crossing.CanGoForward(inVideo: true, core.CanGoForward);
    }

    // Regras cosméticas do Escudo no motor de vídeo (banners, pop-ups): as mesmas do motor principal, registadas pelo
    // DevTools (Browser/VideoPageScript.cs) para correr antes dos scripts da página, uma vez por site e protegidas pelo hostname da página de topo.
    private async Task EnsureVideoCosmeticsAsync(CoreWebView2 core, string url, HashSet<string> done)
    {
        if (HostScriptRegistry.HostOf(url) is not { } page || !done.Add(page.IdnHost)) return;
        var css = SiteCosmeticScript(url);
        if (HostScriptRegistry.Guard(page.IdnHost, css.Length == 0 ? null : css) is not { } script) return;
        await VideoPageScript.AddEarlyAsync(core, script);
    }

    // Retomar o vídeo onde estava no motor principal: um script de uso único, no primeiro «loadedmetadata» da página de
    // topo (Tabs/VideoEngine.cs, ResumeScript). Sai do registo depois da primeira navegação que chegou a ter documento
    // (uma que acabou em transferência desviada para o leitor não conta: é o documento seguinte que precisa dele; uma
    // página de erro conta, para o script não ficar à espera da página seguinte); ficando, cada página seguinte da aba
    // saltava o primeiro vídeo para o mesmo segundo.
    private static async Task ResumeAtAsync(CoreWebView2 core, double seconds)
    {
        if (await VideoPageScript.AddEarlyAsync(core, VideoEngine.ResumeScript(seconds)) is not { } id) return;
        ulong? committed = null;
        void Loading(object? sender, CoreWebView2ContentLoadingEventArgs e) => committed = e.NavigationId;
        async void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (committed != e.NavigationId) return;
            core.ContentLoading -= Loading;
            core.NavigationCompleted -= Completed;
            await VideoPageScript.RemoveEarlyAsync(core, id);
        }
        core.ContentLoading += Loading;
        core.NavigationCompleted += Completed;
    }

    // O motor principal avisou que o vídeo desta página não toca nele (Tabs/VideoEngine.cs, VideoReport).
    private void OnVideoReport(BrowserTab tab, string pageUrl, VideoEngine.VideoReport report)
    {
        if (!_browsers.ContainsKey(tab) || _videoViews.ContainsKey(tab) || !VideoEngine.CanHost(pageUrl)
            || WebHost(pageUrl) is not { } host || !string.Equals(tab.Address, pageUrl, StringComparison.Ordinal)) return;
        var codecs = PlatformCodecs.MainHasCodecs;
        switch (VideoEngine.Decide(report, IsVideoOptOut(host), TrackerBlocker.RegistrableDomain(host.ToLowerInvariant()), codecs))
        {
            case VideoEngine.Decision.Switch:
                // Numa janela privada o site não é lembrado: a lista ficaria em disco depois de a janela fechar.
                if (!IsPrivate) AppData.SetVideoSite(host, true, codecs);
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
            AppData.SetVideoSite(host, true, PlatformCodecs.MainHasCodecs);
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
        if (tab.IsSleeping || !_browsers.ContainsKey(tab)) tab.Crossing.EnterVideo(null);
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
        // Ao mesmo tempo, a página a que o Voltar do motor de vídeo regressa (o histórico do motor principal perde-se).
        var back = MainBackTargetAsync(browser, url);
        var position = await PageScript.EvaluateAsync(browser, VideoEngine.PositionScript, TimeSpan.FromMilliseconds(300));
        PageScript.Run(browser, MediaInfo.PauseScript);
        tab.Crossing.EnterVideo(await back);
        // Só a posição da própria página (Tabs/VideoEngine.cs, ResumesFrom).
        var resumeAt = position.Success && position.Result is double seconds && VideoEngine.ResumesFrom(browser.Address, url) ? seconds : 0;
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

    private static async Task<string?> MainBackTargetAsync(CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string switchingTo)
    {
        try
        {
            if (browser.IsDisposed || browser.GetBrowserHost() is not { } host) return null;
            var read = host.GetNavigationEntriesAsync(currentOnly: false);
            if (await Task.WhenAny(read, Task.Delay(300)) != read) return null;
            var entries = await read;
            // Na página de erro (ou no aviso do «HTTPS primeiro») conta o endereço que falhou, como na omnibox.
            var urls = entries.Select(entry => ErrorPage.IsErrorPage(entry.Url) ? ErrorPage.FailedUrl(entry.Url) ?? entry.Url : entry.Url).ToList();
            return EngineCrossing.BackTarget(urls, entries.FindIndex(entry => entry.IsCurrent), switchingTo);
        }
        catch (Exception) { return null; }
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
            // Na janela privada o perfil não se toca: o site lembrado fica, e o `optedOut` em memória tem prioridade.
            if (IsPrivate) _privateVideoOptOut.Add(TrackerBlocker.RegistrableDomain(host.ToLowerInvariant()));
            else
            {
                AppData.SetVideoSite(host, false);
                AppData.SetVideoOptOut(host, true);
            }
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

    // Da 1.ª entrada do motor de vídeo, o Voltar regressa ao motor principal na página anterior; dali, o Avançar volta
    // ao vídeo (Tabs/EngineCrossing.cs).
    private void GoBack()
    {
        if (Tabs.Active is not { } tab) return;
        if (ActiveVideo is not { } view)
        {
            ReadyBrowser?.Back();
            return;
        }
        if (view.CoreWebView2 is not { } core) return;
        switch (tab.Crossing.Back(inVideo: true, core.CanGoBack))
        {
            case EngineCrossing.Step.InEngine:
                core.GoBack();
                break;
            case EngineCrossing.Step.ToMain when tab.Crossing.ReturnToMain(tab.Address) is { } previous:
                SwitchToNormalEngine(tab, previous);
                break;
        }
    }

    private void GoForward()
    {
        if (Tabs.Active is not { } tab) return;
        if (ActiveVideo is { } view)
        {
            if (view.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
            return;
        }
        if (ReadyBrowser is not { } browser) return;
        switch (tab.Crossing.Forward(inVideo: false, browser.CanGoForward))
        {
            case EngineCrossing.Step.InEngine:
                browser.Forward();
                break;
            case EngineCrossing.Step.ToVideo when tab.Crossing.ForwardToVideo() is { } video:
                SwitchToVideoEngine(tab, video);
                break;
        }
    }

    private void StopActive()
    {
        if (ActiveVideo?.CoreWebView2 is { } core) core.Stop();
        else ReadyBrowser?.Stop();
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
        url = VideoUrl(url);
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

    // ---------- Limpezas no perfil do motor de vídeo ----------
    // «Apagar dados», «Triturar», «Esquecer ao fechar» e os sites de salto chegam também ao que o WebView2 guardou no
    // perfil normal («eteris»). As janelas privadas usam o modo InPrivate do WebView2: nada no disco, nada a limpar.

    // «Apagar dados» (chamado fora do fio da interface): as caixas marcadas, no perfil inteiro do WebView2. False se o
    // perfil existe e não foi possível abri-lo ou apagar.
    internal static async Task<bool> ClearVideoEngineAsync(ClearDataRequest request)
    {
        var kinds = VideoEngine.DataKinds(request.History, request.Cookies, request.Cache);
        if (kinds == 0) return true;
        var done = await Application.Current.Dispatcher.InvokeAsync(() => WithVideoProfileAsync(core => core.Profile.ClearBrowsingDataAsync(kinds))).Task.Unwrap();
        // Os dados dos sites saíram todos do perfil do motor de vídeo: nenhum fica pendente para a varredura do arranque.
        if (done && request.Cookies) AppData.VideoForgetPending.Clear();
        return done;
    }

    // Uma aba viva do motor de vídeo de uma janela normal (o perfil «eteris»), ou nulo. Uma vista a fechar ou caída
    // pode lançar ao ler o CoreWebView2: conta como não haver aba viva.
    private static CoreWebView2? LiveVideoCore()
    {
        try { return NormalWindows().SelectMany(w => w._videoViews.Values).Select(v => v.CoreWebView2).FirstOrDefault(c => c is not null); }
        catch (Exception) { return null; }
    }

    // Corre a ação no perfil normal do motor de vídeo, no fio da interface: numa aba viva dele ou, sem nenhuma, num
    // controlador escondido criado só para isto. Sem a pasta do motor de vídeo (nunca foi usado) não há nada a limpar e
    // o runtime do WebView2 não arranca. Devolve false com qualquer falha e ao fim de VideoEngine.CleanupLimit (um
    // runtime que não responde não deixa «A apagar…» nem a volta dos sites de salto penduradas).
    private static async Task<bool> WithVideoProfileAsync(Func<CoreWebView2, Task> action)
    {
        if (!Directory.Exists(AppPaths.VideoEngine)) return true;
        using var limit = new CancellationTokenSource();
        var work = RunInVideoProfileAsync(action, limit.Token);
        if (await Task.WhenAny(work, Task.Delay(VideoEngine.CleanupLimit)) == work) return await work;
        limit.Cancel();
        return false;
    }

    // O trabalho de WithVideoProfileAsync; nunca lança. expired: o tempo-limite passou e a limpeza já respondeu.
    private static async Task<bool> RunInVideoProfileAsync(Func<CoreWebView2, Task> action, CancellationToken expired)
    {
        if (LiveVideoCore() is { } live)
        {
            try
            {
                await action(live);
                return true;
            }
            catch (Exception) { return false; }
        }

        CoreWebView2Environment environment;
        try { environment = await VideoEnvironment(); }
        catch (Exception)
        {
            // Como em InitializeVideoViewAsync: a próxima tentativa volta a criar o ambiente.
            s_videoEnvironment = null;
            return false;
        }
        CoreWebView2Controller? controller = null;
        try
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "eteris";
            options.IsInPrivateModeEnabled = false;
            // HWND_MESSAGE: a documentação do WebView2 aceita-o como pai de um WebView que nunca se vê — não depende de
            // haver uma janela aberta.
            try { controller = await environment.CreateCoreWebView2ControllerAsync(new IntPtr(-3), options); }
            catch (Exception)
            {
                // O ambiente pode ter deixado de servir (o runtime saiu ou foi atualizado): a próxima tentativa cria outro.
                s_videoEnvironment = null;
                return false;
            }
            // Chegou depois do tempo-limite: a limpeza já respondeu «não foi possível»; fecha-se (finally) sem apagar.
            if (expired.IsCancellationRequested) return false;
            ConfigureVideoPrivacy(controller.CoreWebView2);
            await action(controller.CoreWebView2);
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            try { controller?.Close(); }
            catch (Exception) { }
        }
    }

    // «Triturar», «Esquecer ao fechar» e os sites de salto no motor de vídeo: os cookies do site e dos subdomínios (a
    // regra do Triturar, SiteScope.CookieBelongs) e o armazenamento das origens dadas (uma que falhe não impede as
    // outras). Devolve quantos cookies apagou e se ficou tudo apagado: Complete é false se o perfil do motor de vídeo
    // existe e não se abriu (ou passou o tempo-limite) ou se o motor recusou alguma origem — quem chama di-lo no aviso.
    private static async Task<(int Cookies, bool Complete)> ClearVideoSiteAsync(string site, IReadOnlyList<string> origins)
    {
        var deleted = 0;
        var refused = 0;
        var reached = await WithVideoProfileAsync(async core =>
        {
            var manager = core.CookieManager;
            foreach (var cookie in (await manager.GetCookiesAsync(null)).Where(c => SiteScope.CookieBelongs(c.Domain, site)))
            {
                manager.DeleteCookie(cookie);
                deleted++;
            }
            foreach (var origin in origins)
                if (!await VideoPageScript.ClearOriginAsync(core, origin)) refused++;
        });
        return (deleted, reached && refused == 0);
    }
}
