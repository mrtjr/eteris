using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser;

// «Espreitar»: o destino de um link num cartão por cima da página — título, imagem, descrição, tempo de leitura e
// os primeiros parágrafos — sem abrir aba. A página é carregada numa aba escondida de pré-visualização: sem
// JavaScript nem imagens, num contexto só em memória (sem cookies, como a janela privada), e nada vai para o
// histórico. O artigo é extraído pelo Readability corrido pelo DevTools, que funciona com o JS da página desligado.
public partial class MainWindow
{
    private ChromiumWebBrowser? _glanceBrowser;
    private IRequestContext? _glanceContext;
    private string? _glanceUrl;
    private int _glanceSerial;
    private readonly GlanceLoads _glanceLoads = new();
    // O que o cartão mostra e se está com o artigo inteiro («Ler tudo aqui», tecla E).
    private LinkPreviewInfo? _glanceInfo;
    private bool _glanceExpanded;

    private static readonly TimeSpan GlanceTimeout = TimeSpan.FromSeconds(12);

    // Ajuste e6: fechado o cartão, a aba escondida e o contexto dela vão-se ao fim deste tempo (o renderer do último site
    // ficava vivo até a janela fechar). Reabrir antes disso reaproveita-os; depois, nascem de novo.
    internal static readonly TimeSpan GlanceRelease = TimeSpan.FromSeconds(60);
    private System.Windows.Threading.DispatcherTimer? _glanceReleaseTimer;

    private bool GlanceOpen => GlanceOverlay.Visibility == Visibility.Visible;

    private void ShowLinkPreview(string url)
    {
        if (!PageMenu.IsWebLink(url)) return;
        var serial = ++_glanceSerial;
        _glanceUrl = url;
        _glanceReleaseTimer?.Stop();
        ResetGlanceCard(url);
        GlanceReadLater.Visibility = IsPrivate ? Visibility.Collapsed : Visibility.Visible;
        if (!GlanceOpen)
        {
            GlanceOverlay.Visibility = Visibility.Visible;
            AnimateGlanceIn();
        }
        FocusInOverlay(GlanceOverlay);

        var browser = EnsureGlanceBrowser(url);
        if (browser.IsBrowserInitialized) _ = LoadGlanceAsync(browser, url, serial);
        else browser.Address = url;
        _ = GlanceWatchdog(serial);
    }

    // Cada espreitadela começa sem os cookies das anteriores: o contexto é só em memória, mas dura a janela inteira,
    // e um cookie de sessão devolvido pelo servidor ligava as espreitadelas ao mesmo site umas às outras.
    private async Task LoadGlanceAsync(ChromiumWebBrowser browser, string url, int serial)
    {
        await ClearGlanceCookiesAsync();
        if (serial != _glanceSerial || browser.IsDisposed) return;
        browser.Load(url);
    }

    private async Task ClearGlanceCookiesAsync()
    {
        try
        {
            if (_glanceContext?.GetCookieManager(null) is { } cookies) await cookies.DeleteCookiesAsync(string.Empty, string.Empty);
        }
        catch (Exception) { }
    }

    private void CloseGlance()
    {
        if (!GlanceOpen) return;
        _glanceSerial++;
        _glanceUrl = null;
        _glanceInfo = null;
        _glanceExpanded = false;
        GlanceOverlay.Visibility = Visibility.Collapsed;
        GlanceImage.ImageSource = null;
        // A página pré-visualizada é largada: a aba escondida não fica a ocupar memória com ela.
        if (_glanceBrowser is { IsDisposed: false, IsBrowserInitialized: true } browser) browser.Load("about:blank");
        _ = ClearGlanceCookiesAsync();
        ReturnFocusToPage();
        ScheduleGlanceRelease();
    }

    // Ajuste e6 (Infrastructure/PerfTuning.cs): o motor do Espreitar não fica à espera até a janela fechar.
    private void ScheduleGlanceRelease()
    {
        if (!Infrastructure.PerfTuning.IsOn(Infrastructure.PerfTuning.ReleaseGlance) || _glanceBrowser is null) return;
        _glanceReleaseTimer ??= new System.Windows.Threading.DispatcherTimer(GlanceRelease, System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => ReleaseGlance(), Dispatcher);
        _glanceReleaseTimer.Stop();
        _glanceReleaseTimer.Start();
    }

    private void ReleaseGlance()
    {
        _glanceReleaseTimer?.Stop();
        if (GlanceOpen) return;
        if (_glanceBrowser is { } browser)
        {
            _glanceBrowser = null;
            BrowserHost.Children.Remove(browser);
            PageScript.Release(browser);
            if (!browser.IsDisposed) browser.Dispose();
        }
        _glanceContext?.Dispose();
        _glanceContext = null;
    }

    private void ResetGlanceCard(string url)
    {
        _glanceInfo = null;
        _glanceExpanded = false;
        GlanceSite.Text = LinkPreview.HostOf(url);
        ShowGlanceSiteIcon(null, GlanceSite.Text);
        GlanceExpand.Visibility = Visibility.Collapsed;
        GlanceReading.Text = string.Empty;
        GlanceTitle.Text = LinkPreview.HostOf(url);
        NameGlanceCard(GlanceTitle.Text);
        GlanceByline.Visibility = Visibility.Collapsed;
        GlanceDescription.Visibility = Visibility.Collapsed;
        GlanceImageHost.Visibility = Visibility.Collapsed;
        GlanceImage.ImageSource = null;
        GlanceParagraphs.Children.Clear();
        GlanceScrollViewer.ScrollToTop();
        GlanceUrl.Text = UrlCleaner.Clean(url);
        SetGlanceStatus("A espreitar…", busy: true);
    }

    private void SetGlanceStatus(string? text, bool busy)
    {
        GlanceStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        GlanceSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GlanceStatusText.Text = text ?? string.Empty;
        // Região viva educada: o leitor de ecrã ouve «A espreitar…», o erro ou «Sem resumo» sem tirar o foco do cartão.
        if (text is not null) Sync.LiveRegion.Announce(GlanceStatusText);
    }

    // O nome acessível do cartão. Um Border não tem par de automação: o nome vai também no ScrollViewer do cartão, que
    // o leitor de ecrã vê.
    private void NameGlanceCard(string title)
    {
        AutomationProperties.SetName(GlanceCard, "Espreitar: " + title);
        AutomationProperties.SetName(GlanceScrollViewer, "Espreitar: " + title);
    }

    // O ícone do site: o favicon guardado na cache local de ícones do histórico (a das listas, só leitura, também na
    // janela privada, que não escreve nela) ou, sem ele, a 1.ª letra do site num círculo. Nenhum pedido. Lê-se uma vez,
    // quando a página chega (pageUrl: o endereço final, depois de redirecionamentos), direto da base e não da cache de
    // ícones da janela, que não sabe de um histórico apagado. A carregar (pageUrl null), só o monograma. O favicon tem o
    // mesmo nome acessível do monograma: o leitor de ecrã ouve o cabeçalho igual, com ou sem ícone.
    private void ShowGlanceSiteIcon(string? pageUrl, string site)
    {
        var icon = pageUrl is not null && HistoryStore.SiteIconKey(pageUrl) is { } key
            && AppData.SiteIcons([pageUrl]).TryGetValue(key, out var dataUri) ? DecodeDataUri(dataUri) : null;
        GlanceFavicon.Source = icon;
        GlanceFavicon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        GlanceMonogram.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        GlanceMonogramText.Text = LinkPreview.Monogram(site);
        AutomationProperties.SetName(GlanceMonogramText, site);
        AutomationProperties.SetName(GlanceFavicon, site);
    }

    private void AnimateGlanceIn()
    {
        if (GlanceCard.RenderTransform is not ScaleTransform scale) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        GlanceCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    // Uma só aba escondida por janela, criada na primeira vez e reaproveitada; morre com a janela.
    private ChromiumWebBrowser EnsureGlanceBrowser(string url)
    {
        if (_glanceBrowser is { IsDisposed: false } existing) return existing;
        _glanceContext ??= CreatePrivateContext(onBlocked: null);
        var browser = new ChromiumWebBrowser(url)
        {
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false,
            Focusable = false,
            BrowserSettings = new BrowserSettings
            {
                Javascript = CefState.Disabled,
                ImageLoading = CefState.Disabled,
                LocalStorage = CefState.Disabled,
                Databases = CefState.Disabled,
            },
            RequestContext = _glanceContext,
            // Nunca rouba o teclado à página nem abre o que quer que seja: pop-ups, transferências e menus ficam mudos.
            FocusHandler = new SilentFocusHandler(),
            LifeSpanHandler = new PopupToTabHandler(_ => { }),
            // Um link para um ficheiro (zip, pdf, exe) vira transferência: é recusada e o cartão diz logo porquê, em vez
            // de ficar a girar até ao limite de tempo.
            DownloadHandler = new SilentDownloadHandler(() =>
            {
                var serial = Volatile.Read(ref _glanceSerial);
                Dispatcher.InvokeAsync(() =>
                {
                    if (serial == _glanceSerial && GlanceOpen) SetGlanceStatus(LinkPreview.FileStatus, busy: false);
                });
            }),
            MenuHandler = new SilentMenuHandler(),
            PermissionHandler = PermissionHandler,
            // A rede local que o utilizador deixou ao site também chega na pré-visualização (sem cartão: a pré-visualização
            // não pergunta nada). Um pedido de certificado de cliente é recusado, sem diálogo.
            RequestHandler = new PrivacyRequestHandler(() => ShieldEngine.Current, ShieldOn, onBlocked: _ => { }, isPrivate: IsPrivate,
                localNetworkAllowed: IsLocalNetworkAllowed, chooseCertificate: RefuseClientCertificate),
        };
        // O pedido a que o documento pertence fixa-se quando ele é confirmado (ver GlanceLoads).
        browser.FrameLoadStart += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            // Conteúdo web confirmado: a rede de segurança do sandbox deixa de contar quedas dos filhos (INF-01).
            if (Infrastructure.SandboxFallback.IsWebContent(e.Url)) Infrastructure.StartupGuard.Current.WebContentCommitted();
            _glanceLoads.Committed(Volatile.Read(ref _glanceSerial));
        };
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain || _glanceLoads.Finished() is not { } serial) return;
            var loaded = e.Url;
            Dispatcher.InvokeAsync(() => _ = ExtractGlanceAsync(browser, loaded, serial));
        };
        browser.LoadError += (_, e) =>
        {
            if (!e.Frame.IsMain || e.ErrorCode == CefErrorCode.Aborted) return;
            var serial = _glanceSerial;
            var code = (int)e.ErrorCode;
            Dispatcher.InvokeAsync(() =>
            {
                if (serial == _glanceSerial && GlanceOpen) SetGlanceStatus($"Não foi possível carregar a página (erro {code})", busy: false);
            });
        };
        Grid.SetColumnSpan(browser, 3);
        Grid.SetRowSpan(browser, 3);
        BrowserHost.Children.Insert(0, browser);
        Closed += (_, _) =>
        {
            _glanceReleaseTimer?.Stop();
            PageScript.Release(browser);
            // Já largado pelo ajuste e6 (ReleaseGlance): nada a fazer com este.
            if (!browser.IsDisposed) browser.Dispose();
            _glanceContext?.Dispose();
            _glanceContext = null;
        };
        _glanceBrowser = browser;
        return browser;
    }

    private async Task ExtractGlanceAsync(ChromiumWebBrowser browser, string loadedUrl, int serial)
    {
        if (serial != _glanceSerial || _glanceUrl is not { } url || !GlanceOpen) return;
        if (loadedUrl == "about:blank") return;
        try
        {
            var response = await PageScript.EvaluateAsync(browser, LinkPreview.Script, TimeSpan.FromSeconds(6));
            if (serial != _glanceSerial || !GlanceOpen) return;
            // O cartão descreve a página lida (ícone, nome do site, marca no título, og:image relativa): depois de um
            // redirecionamento, a do destino. «Abrir» e «Ler mais tarde» continuam com o link (_glanceUrl).
            var page = LinkPreview.PageUrl(url, loadedUrl);
            var info = response.Success ? LinkPreview.Parse(response.Result as string, page) : null;
            if (info is null)
            {
                SetGlanceStatus("Não foi possível ler esta página", busy: false);
                return;
            }
            FillGlance(info);
            if (info.ImageUrl is { } image) LoadGlanceImage(browser, image, page, serial);
        }
        catch (Exception)
        {
            if (serial == _glanceSerial && GlanceOpen) SetGlanceStatus("Não foi possível ler esta página", busy: false);
        }
    }

    private void FillGlance(LinkPreviewInfo info)
    {
        _glanceInfo = info;
        _glanceExpanded = false;
        GlanceSite.Text = info.Site;
        ShowGlanceSiteIcon(info.Url, info.Site);
        GlanceReading.Text = info.ReadingTime.Length > 0 ? "· " + info.ReadingTime : string.Empty;
        GlanceTitle.Text = info.Title;
        NameGlanceCard(info.Title);
        GlanceByline.Text = info.Byline ?? string.Empty;
        GlanceByline.Visibility = info.Byline is null ? Visibility.Collapsed : Visibility.Visible;
        GlanceDescription.Text = info.Description ?? string.Empty;
        GlanceDescription.Visibility = info.Description is null ? Visibility.Collapsed : Visibility.Visible;
        ShowGlanceParagraphs(info.Paragraphs);
        // «Ler tudo aqui» só quando o artigo tem mais do que o resumo mostra.
        GlanceExpand.Visibility = info.HasMore ? Visibility.Visible : Visibility.Collapsed;
        GlanceExpand.Content = LinkPreview.ExpandLabel(expanded: false);
        // A dica de um cartão anterior expandido («Voltar ao resumo») não fica: o leitor de ecrã lê-a como ajuda.
        GlanceExpand.ToolTip = LinkPreview.ExpandTip(expanded: false);
        var status = info.Paragraphs.Count == 0 && info.Description is null ? LinkPreview.NoSummaryStatus : null;
        SetGlanceStatus(status, busy: false);
        // Com o resumo pronto, o estado some sem nada dizer: o leitor de ecrã ouve o título (região viva educada).
        if (status is null) Sync.LiveRegion.Announce(GlanceTitle);
    }

    private void ShowGlanceParagraphs(IReadOnlyList<string> paragraphs)
    {
        GlanceParagraphs.Children.Clear();
        foreach (var paragraph in paragraphs)
            GlanceParagraphs.Children.Add(new TextBlock
            {
                Text = paragraph,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5,
                LineHeight = 21,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 10),
            });
    }

    // «Ler tudo aqui» (botão ou tecla E): o artigo inteiro (FullParagraphs, sem o corte do resumo) rola dentro do
    // cartão, que cresce até à altura máxima; «Mostrar menos» volta ao resumo. Continua sem JavaScript nem cookies: o
    // texto já veio da aba escondida, não há pedido novo. Sem nada a mais (botão escondido), a tecla não faz nada.
    private void ToggleGlanceExpand()
    {
        if (_glanceInfo is not { HasMore: true } info || GlanceExpand.Visibility != Visibility.Visible) return;
        _glanceExpanded = !_glanceExpanded;
        var offset = GlanceScrollViewer.VerticalOffset;
        ShowGlanceParagraphs(_glanceExpanded ? info.FullParagraphs : info.Paragraphs);
        GlanceExpand.Content = LinkPreview.ExpandLabel(_glanceExpanded);
        GlanceExpand.ToolTip = LinkPreview.ExpandTip(_glanceExpanded);
        // A recolher, o resumo cabe acima do ponto onde se estava: volta-se ao princípio. A expandir, fica onde estava.
        if (_glanceExpanded) GlanceScrollViewer.ScrollToVerticalOffset(offset);
        else GlanceScrollViewer.ScrollToTop();
    }

    private void GlanceExpand_Click(object sender, RoutedEventArgs e) => ToggleGlanceExpand();

    // Imagem do cartão (og:image): pedida pelo próprio Chromium a partir da aba escondida — passa pelo Escudo, pelo
    // DNS cifrado, pelo proxy e pelo contexto só em memória, sem cookies nem referrer. A pilha de rede do .NET não via
    // nada disto e resolvia o nome do servidor da imagem em claro (ver MainWindow.Favicons.cs).
    private void LoadGlanceImage(ChromiumWebBrowser browser, string imageUrl, string pageUrl, int serial)
    {
        // Um pixel de rastreio em og:image não é pedido.
        if (ShieldOn(LinkPreview.HostOf(pageUrl)) && ShieldEngine.Current.ShouldBlock(imageUrl, pageUrl, "image")) return;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        if (browser.IsDisposed || !browser.IsBrowserInitialized) return;
        var client = new EngineFetchClient(bytes => _ = DecodeAndShowGlanceImageAsync(bytes, serial),
            LinkPreview.MaxImageBytes, LinkPreview.AcceptsImage);
        _ = Cef.UIThreadTaskFactory.StartNew(() =>
        {
            try
            {
                if (browser.IsDisposed) { client.Fail(); return; }
                using var frame = browser.GetBrowser()?.MainFrame;
                if (frame is null) { client.Fail(); return; }
                using var request = Request.Create();
                request.Url = uri.AbsoluteUri;
                request.SetReferrer(string.Empty, ReferrerPolicy.NoReferrer);
                request.SetHeaderByName("Accept", "image/*", true);
                request.SetHeaderByName("Sec-GPC", "1", true);
                request.Flags = UrlRequestFlags.SkipCache;
                client.Start(frame.CreateUrlRequest(request, client));
            }
            catch (Exception) { client.Fail(); }
        });
    }

    // Chamado nas threads do CEF: assinatura e dimensões pelo cabeçalho, descodificação fora da thread da janela.
    private async Task DecodeAndShowGlanceImageAsync(byte[]? bytes, int serial)
    {
        try
        {
            if (bytes is null || bytes.Length == 0) return;
            // Cartão já fechado, ou outro aberto entretanto: nem se gasta a descodificação (o estado só se lê na thread da janela).
            if (!await Dispatcher.InvokeAsync(() => serial == _glanceSerial && GlanceOpen)) return;
            var image = await Task.Run(() => DecodeGlanceImage(bytes));
            if (image is null) return;
            await Dispatcher.InvokeAsync(() => ShowGlanceImage(image, serial));
        }
        catch (Exception) { }
    }

    private static BitmapSource? DecodeGlanceImage(byte[] bytes)
    {
        if (!ImageSniff.Accepts(bytes, ImageSniff.MaxPreviewSide)) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1080;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    private void ShowGlanceImage(BitmapSource image, int serial)
    {
        if (serial != _glanceSerial || !GlanceOpen) return;
        GlanceImage.ImageSource = image;
        GlanceImageHost.Visibility = Visibility.Visible;
    }

    private async Task GlanceWatchdog(int serial)
    {
        await Task.Delay(GlanceTimeout);
        if (serial != _glanceSerial || !GlanceOpen || GlanceSpinner.Visibility != Visibility.Visible) return;
        SetGlanceStatus("A página não respondeu a tempo", busy: false);
        _glanceBrowser?.Stop();
    }

    private void GlanceOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, GlanceOverlay)) CloseGlance();
    }

    private void GlanceOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var action = LinkPreview.ActionFor(key.ToString(), shift, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), IsPrivate);
        // Alt+E fica para os atalhos da janela: só a tecla E sozinha expande.
        if (action == GlanceAction.Expand && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) action = GlanceAction.None;
        // A E é um interruptor: com ela premida, a repetição automática do teclado alternava sem parar entre o resumo e
        // o artigo inteiro (o cartão a piscar e a saltar para o topo). Só o primeiro toque conta.
        if (action == GlanceAction.Expand && e.IsRepeat)
        {
            e.Handled = true;
            return;
        }
        if (action != GlanceAction.None)
        {
            e.Handled = true;
            RunGlanceAction(action);
            return;
        }
        // O foco fica na camada (para o Esc e o Enter): as teclas de rolagem passam ao cartão, senão o que fica
        // abaixo da dobra em janelas pequenas só se via com o rato.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var scroll = LinkPreview.ScrollFor(key.ToString(), shift);
        if (scroll == GlanceScroll.None) return;
        e.Handled = true;
        switch (scroll)
        {
            case GlanceScroll.LineUp: GlanceScrollViewer.LineUp(); break;
            case GlanceScroll.LineDown: GlanceScrollViewer.LineDown(); break;
            case GlanceScroll.PageUp: GlanceScrollViewer.PageUp(); break;
            case GlanceScroll.PageDown: GlanceScrollViewer.PageDown(); break;
            case GlanceScroll.Top: GlanceScrollViewer.ScrollToTop(); break;
            case GlanceScroll.Bottom: GlanceScrollViewer.ScrollToBottom(); break;
        }
    }

    private void RunGlanceAction(GlanceAction action)
    {
        switch (action)
        {
            case GlanceAction.Close: CloseGlance(); break;
            case GlanceAction.Open: OpenGlanceInTab(); break;
            case GlanceAction.OpenSideBySide: OpenGlanceSideBySide(); break;
            case GlanceAction.ReadLater: SaveGlanceForLater(); break;
            case GlanceAction.Expand: ToggleGlanceExpand(); break;
        }
    }

    // O cartão nunca é mais alto do que a área da página (janelas pequenas, modo compacto): o resto rola.
    private void GlanceOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        => GlanceCard.MaxHeight = Math.Max(240, GlanceOverlay.ActualHeight - 48);

    // Mudar de aba fecha o cartão: ele pertence à página de onde veio o link.
    private void CloseGlanceOnTabChange()
    {
        if (GlanceOpen) CloseGlance();
    }

    private void GlanceClose_Click(object sender, RoutedEventArgs e) => CloseGlance();

    private void GlanceOpen_Click(object sender, RoutedEventArgs e) => OpenGlanceInTab();

    private void OpenGlanceInTab()
    {
        if (_glanceUrl is not { } url) return;
        CloseGlance();
        Tabs.NewTab(url);
    }

    private void GlanceSplit_Click(object sender, RoutedEventArgs e) => OpenGlanceSideBySide();

    private void OpenGlanceSideBySide()
    {
        if (_glanceUrl is not { } url) return;
        CloseGlance();
        OpenSideBySide(Tabs.NewTab(url, activate: false));
    }

    private void GlanceReadLater_Click(object sender, RoutedEventArgs e) => SaveGlanceForLater();

    private void SaveGlanceForLater()
    {
        if (_glanceUrl is not { } url || IsPrivate) return;
        AppData.SaveForLater(url, GlanceTitle.Text);
        CloseGlance();
        ShowToast("Guardado em «Para ler» — veja em Favoritos › Para ler");
    }
}
