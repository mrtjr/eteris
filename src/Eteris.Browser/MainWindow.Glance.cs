using System.IO;
using System.Net.Http;
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

    private static readonly TimeSpan GlanceTimeout = TimeSpan.FromSeconds(12);

    // Imagem do cartão (og:image): um pedido único, sem cookies nem referrer, só quando o escudo o deixa passar.
    private static readonly HttpClient GlanceHttp = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 4 })
    {
        Timeout = TimeSpan.FromSeconds(8),
        MaxResponseContentBufferSize = 4 * 1024 * 1024,
    };

    private bool GlanceOpen => GlanceOverlay.Visibility == Visibility.Visible;

    private void ShowLinkPreview(string url)
    {
        if (!PageMenu.IsWebLink(url)) return;
        var serial = ++_glanceSerial;
        _glanceUrl = url;
        ResetGlanceCard(url);
        GlanceReadLater.Visibility = IsPrivate ? Visibility.Collapsed : Visibility.Visible;
        if (!GlanceOpen)
        {
            GlanceOverlay.Visibility = Visibility.Visible;
            AnimateGlanceIn();
        }
        FocusInOverlay(GlanceOverlay);

        var browser = EnsureGlanceBrowser(url);
        if (browser.IsBrowserInitialized) browser.Load(url);
        else browser.Address = url;
        _ = GlanceWatchdog(serial);
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
        ReturnFocusToPage();
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
        _glanceContext ??= CreatePrivateContext();
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
            DownloadHandler = new SilentDownloadHandler(),
            MenuHandler = new SilentMenuHandler(),
            PermissionHandler = PermissionHandler,
            RequestHandler = new PrivacyRequestHandler(() => ShieldEngine.Current, AppData.IsShieldOn,
                onNavigate: (_, _) => Task.CompletedTask, onBlocked: _ => { }),
        };
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            var loaded = e.Url;
            var serial = _glanceSerial;
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
        BrowserHost.Children.Insert(0, browser);
        Closed += (_, _) =>
        {
            PageScript.Release(browser);
            browser.Dispose();
            _glanceContext?.Dispose();
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
            if (info.ImageUrl is { } image) _ = LoadGlanceImageAsync(image, url, serial);
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

    private async Task LoadGlanceImageAsync(string imageUrl, string pageUrl, int serial)
    {
        // A imagem passa pelo mesmo escudo que a página: um pixel de rastreio em og:image não é carregado.
        if (AppData.IsShieldOn(LinkPreview.HostOf(pageUrl)) && ShieldEngine.Current.ShouldBlock(imageUrl, pageUrl, "image")) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            request.Headers.TryAddWithoutValidation("Accept", "image/*");
            request.Headers.TryAddWithoutValidation("Sec-GPC", "1");
            using var response = await GlanceHttp.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            if (!response.IsSuccessStatusCode) return;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (serial != _glanceSerial || !GlanceOpen || bytes.Length == 0) return;
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
        if (e.Key == Key.Escape) { CloseGlance(); e.Handled = true; }
        else if (e.Key == Key.Enter) { OpenGlanceInTab(); e.Handled = true; }
    }

    private void GlanceClose_Click(object sender, RoutedEventArgs e) => CloseGlance();

    private void GlanceOpen_Click(object sender, RoutedEventArgs e) => OpenGlanceInTab();

    private void OpenGlanceInTab()
    {
        if (_glanceUrl is not { } url) return;
        CloseGlance();
        Tabs.NewTab(url);
    }

    private void GlanceSplit_Click(object sender, RoutedEventArgs e)
    {
        if (_glanceUrl is not { } url) return;
        CloseGlance();
        OpenSideBySide(Tabs.NewTab(url, activate: false));
    }

    private void GlanceReadLater_Click(object sender, RoutedEventArgs e)
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

    private sealed class SilentDownloadHandler : CefSharp.Handler.DownloadHandler
    {
        protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod) => false;
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
