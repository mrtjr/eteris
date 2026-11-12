using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        GlanceSite.Text = LinkPreview.HostOf(url);
        GlanceReading.Text = string.Empty;
        GlanceTitle.Text = LinkPreview.HostOf(url);
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
            // não pergunta nada).
            RequestHandler = new PrivacyRequestHandler(() => ShieldEngine.Current, ShieldOn, onBlocked: _ => { }, isPrivate: IsPrivate,
                localNetworkAllowed: IsLocalNetworkAllowed),
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
            var info = response.Success ? LinkPreview.Parse(response.Result as string, url) : null;
            if (info is null)
            {
                SetGlanceStatus("Não foi possível ler esta página", busy: false);
                return;
            }
            FillGlance(info);
            if (info.ImageUrl is { } image) LoadGlanceImage(browser, image, url, serial);
        }
        catch (Exception)
        {
            if (serial == _glanceSerial && GlanceOpen) SetGlanceStatus("Não foi possível ler esta página", busy: false);
        }
    }

    private void FillGlance(LinkPreviewInfo info)
    {
        GlanceSite.Text = info.Site;
        GlanceReading.Text = info.ReadingTime.Length > 0 ? "· " + info.ReadingTime : string.Empty;
        GlanceTitle.Text = info.Title;
        GlanceByline.Text = info.Byline ?? string.Empty;
        GlanceByline.Visibility = info.Byline is null ? Visibility.Collapsed : Visibility.Visible;
        GlanceDescription.Text = info.Description ?? string.Empty;
        GlanceDescription.Visibility = info.Description is null ? Visibility.Collapsed : Visibility.Visible;
        GlanceParagraphs.Children.Clear();
        foreach (var paragraph in info.Paragraphs)
            GlanceParagraphs.Children.Add(new TextBlock
            {
                Text = paragraph,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5,
                LineHeight = 21,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 10),
            });
        SetGlanceStatus(info.Paragraphs.Count == 0 && info.Description is null ? "Esta página não tem texto para mostrar" : null, busy: false);
    }

    // Imagem do cartão (og:image): pedida pelo próprio Chromium a partir da aba escondida — passa pelo Escudo, pelo
    // DNS cifrado, pelo proxy e pelo contexto só em memória, sem cookies nem referrer. A pilha de rede do .NET não via
    // nada disto e resolvia o nome do servidor da imagem em claro (ver MainWindow.Favicons.cs).
    private void LoadGlanceImage(ChromiumWebBrowser browser, string imageUrl, string pageUrl, int serial)
    {
        // Um pixel de rastreio em og:image não é pedido.
        if (ShieldOn(LinkPreview.HostOf(pageUrl)) && ShieldEngine.Current.ShouldBlock(imageUrl, pageUrl, "image")) return;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        if (browser.IsDisposed || !browser.IsBrowserInitialized) return;
        var client = new EngineFetchClient(bytes => Dispatcher.InvokeAsync(() => ShowGlanceImage(bytes, serial)),
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

    private void ShowGlanceImage(byte[]? bytes, int serial)
    {
        if (bytes is null || serial != _glanceSerial || !GlanceOpen || bytes.Length == 0) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1080;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            GlanceImage.ImageSource = image;
            GlanceImageHost.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
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

    private sealed class SilentFocusHandler : IFocusHandler
    {
        public void OnGotFocus(IWebBrowser chromiumWebBrowser, IBrowser browser) { }
        public bool OnSetFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, CefFocusSource source) => true;
        public void OnTakeFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, bool next) { }
    }

    private sealed class SilentDownloadHandler(Action onRefused) : CefSharp.Handler.DownloadHandler
    {
        protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
        {
            onRefused();
            return false;
        }
    }

    private sealed class SilentMenuHandler : IContextMenuHandler
    {
        public void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
            => model.Clear();
        public bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters,
            CefMenuCommand commandId, CefEventFlags eventFlags) => false;
        public void OnContextMenuDismissed(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }
        public bool RunContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model,
            IRunContextMenuCallback callback)
        {
            callback.Cancel();
            return true;
        }
    }
}
