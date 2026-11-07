using System.IO;
using System.Windows.Media.Imaging;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;
using Microsoft.Web.WebView2.Core;

namespace Eteris.Browser;

public partial class MainWindow
{
    private const int FaviconSize = 32;

    // Um pedido de ícone de cada vez por aba: o último anunciado ganha, mesmo que o anterior ainda esteja a chegar.
    private readonly Dictionary<BrowserTab, object> _faviconRequests = new();

    private void SetFavicon(BrowserTab tab, string? url, string? pageUrl)
    {
        if (HistoryStore.SiteIconKey(pageUrl) is not { } site || site != HistoryStore.SiteIconKey(tab.Address)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            _faviconRequests.Remove(tab);
            tab.Favicon = null;
            return;
        }
        if (!_browsers.TryGetValue(tab, out var browser) || browser.IsDisposed || !browser.IsBrowserInitialized) return;

        // O ícone é pedido pelo próprio Chromium, a partir do quadro da página: passa pelo Escudo, pelo DNS cifrado,
        // pelo proxy e pelo contexto da janela privada como qualquer outro recurso do site. A pilha de rede do .NET
        // (BitmapImage.UriSource) não via nada disto e saía em claro pelo resolvedor do sistema.
        var token = new object();
        _faviconRequests[tab] = token;
        var client = new EngineFetchClient(bytes => Dispatcher.InvokeAsync(() => ApplyFavicon(tab, token, site, pageUrl!, bytes)),
            FaviconHandler.MaxBytes, FaviconHandler.AcceptsResponse);
        _ = Cef.UIThreadTaskFactory.StartNew(() =>
        {
            try
            {
                if (browser.IsDisposed) { client.Fail(); return; }
                using var frame = browser.GetBrowser()?.MainFrame;
                if (frame is null) { client.Fail(); return; }
                using var request = Request.Create();
                request.Url = uri.AbsoluteUri;
                request.SetReferrer(pageUrl, ReferrerPolicy.Default);
                // Sem credenciais guardadas: o ícone não precisa de cookies. Na janela privada nem a cache em memória conta.
                request.Flags = IsPrivate ? UrlRequestFlags.SkipCache : UrlRequestFlags.None;
                client.Start(frame.CreateUrlRequest(request, client));
            }
            catch (Exception) { client.Fail(); }
        });
    }

    // Já na thread da janela, com os bytes vindos do motor (ou null se o pedido falhou, foi bloqueado ou não serve).
    private void ApplyFavicon(BrowserTab tab, object token, string site, string pageUrl, byte[]? bytes)
    {
        // Entretanto a aba pode ter mudado de site ou anunciado outro ícone: só o pedido mais recente conta.
        if (!_faviconRequests.TryGetValue(tab, out var current) || !ReferenceEquals(current, token)) return;
        _faviconRequests.Remove(tab);
        if (site != HistoryStore.SiteIconKey(tab.Address)) return;
        if (bytes is null)
        {
            tab.Favicon = null;
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = FaviconSize;
            image.DecodePixelHeight = FaviconSize;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            tab.FaviconSite = site;
            tab.Favicon = image;
            RememberFavicon(tab, pageUrl);
        }
        catch (Exception) { tab.Favicon = null; }
    }

    // Pedido feito pelo próprio motor (ícones, imagem do Espreitar). Recebe a resposta nas threads do CEF: só junta
    // os bytes (até maxBytes) e entrega tudo no fim, se accepts(estado, tipo, tamanho) aceitar a resposta.
    private sealed class EngineFetchClient(Action<byte[]?> onDone, int maxBytes, Func<int, string?, long, bool> accepts) : IUrlRequestClient
    {
        private readonly MemoryStream _bytes = new();
        private IUrlRequest? _request;
        private bool _tooBig;

        // Guarda o pedido até ao fim: a vida do objeto do CEF não fica à mercê do coletor de lixo.
        public void Start(IUrlRequest? request)
        {
            _request = request;
            if (request is null) Fail();
        }

        public void Fail() => onDone(null);

        bool IUrlRequestClient.GetAuthCredentials(bool isProxy, string host, int port, string realm, string scheme, IAuthCallback callback)
        {
            // Um ícone ou uma imagem nunca justificam pedir credenciais ao utilizador.
            callback.Dispose();
            return false;
        }

        void IUrlRequestClient.OnDownloadData(IUrlRequest request, Stream data)
        {
            if (_tooBig) return;
            var chunk = new byte[8192];
            int read;
            while ((read = data.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (_bytes.Length + read > maxBytes)
                {
                    _tooBig = true;
                    _bytes.SetLength(0);
                    return;
                }
                _bytes.Write(chunk, 0, read);
            }
        }

        void IUrlRequestClient.OnDownloadProgress(IUrlRequest request, long current, long total) { }

        void IUrlRequestClient.OnUploadProgress(IUrlRequest request, long current, long total) { }

        void IUrlRequestClient.OnRequestComplete(IUrlRequest request)
        {
            var response = request.Response;
            var usable = !_tooBig && request.RequestStatus == UrlRequestStatus.Success
                && accepts(response?.StatusCode ?? 0, response?.MimeType, _bytes.Length);
            onDone(usable ? _bytes.ToArray() : null);
            _bytes.Dispose();
            // Mesma thread do CEF que criou o pedido, como a API exige.
            _request?.Dispose();
            _request = null;
        }
    }

    private async Task SetVideoFaviconAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            var pageUrl = core.Source;
            if (HistoryStore.SiteIconKey(pageUrl) is not { } site) return;
            using var png = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (tab.Address != pageUrl || core.Source != pageUrl) return;
            if (png is null || !png.CanRead)
            {
                tab.Favicon = null;
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = png;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = FaviconSize;
            image.DecodePixelHeight = FaviconSize;
            image.EndInit();
            tab.FaviconSite = site;
            tab.Favicon = image;
            RememberFavicon(tab, pageUrl);
        }
        catch (Exception) { }
    }

    private void RememberFavicon(BrowserTab tab, string pageUrl)
    {
        if (IsPrivate || tab.Address != pageUrl || tab.FaviconSite != HistoryStore.SiteIconKey(pageUrl)
            || tab.Favicon is not BitmapSource image) return;

        try
        {
            if (image.PixelWidth is <= 0 or > FaviconSize || image.PixelHeight is <= 0 or > FaviconSize) return;
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image, null, null, null));
            using var stream = new MemoryStream();
            png.Save(stream);
            var bytes = stream.ToArray();
            AppData.QueueWrite(history => history.SaveSiteIcon(pageUrl, bytes));
        }
        catch (Exception) { }
    }
}
