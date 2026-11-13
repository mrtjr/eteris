using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using CefSharp;

namespace Eteris.Browser.Downloads;

// Uma instância para todas as abas: transfere sempre para a pasta Transferências, sem diálogo. divert pode
// recusar uma transferência antes de começar (o Alt+clique nativo do Chromium, que o Éteris usa para espreitar).
// play recebe as «transferências» que são afinal vídeo/áudio para ver (HLS .m3u8, AAC .m4a, MP4 sem «attachment»):
// sem os codecs patenteados o motor principal não os reconhece e transferia-os; devolve true quando os abriu no motor
// de vídeo. As transferências pedidas pelo utilizador (Guardar imagem…) passam por UserRequested e nunca são desviadas.
public sealed class DownloadHandler(DownloadsViewModel downloads, Dispatcher dispatcher, Action<IWebBrowser, string> onStarted,
    Func<IWebBrowser, string, bool>? divert = null, Func<IWebBrowser, string, bool>? play = null)
    : CefSharp.Handler.DownloadHandler
{
    private readonly ConcurrentDictionary<int, IDownloadItemCallback> _callbacks = new();
    private readonly ConcurrentDictionary<string, DateTime> _userRequested = new(StringComparer.Ordinal);

    // Chamar antes de StartDownload: este endereço é para guardar, mesmo que seja vídeo.
    public void UserRequested(string url)
    {
        var now = DateTime.UtcNow;
        foreach (var (old, at) in _userRequested)
            if (now - at > TimeSpan.FromMinutes(1)) _userRequested.TryRemove(old, out _);
        _userRequested[url] = now;
    }

    protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
    {
        if (divert?.Invoke(chromiumWebBrowser, url) == true) return false;
        // HLS pela extensão logo aqui (nunca é um ficheiro que se queira guardar); o resto vai pelo tipo, mais abaixo.
        if (play is not null && !_userRequested.ContainsKey(url) && Tabs.VideoEngine.IsPlayableLink(url)
            && url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) && play(chromiumWebBrowser, url)) return false;
        return true;
    }

    protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
        IBeforeDownloadCallback callback)
    {
        var original = downloadItem.OriginalUrl;
        if (play is not null && !_userRequested.TryRemove(original, out _)
            && Tabs.VideoEngine.IsPlayableDownload(original, downloadItem.MimeType, downloadItem.ContentDisposition)
            && play(chromiumWebBrowser, original))
        {
            // Sem Continue: ao libertar a chamada, o motor cancela a transferência (nada fica no disco).
            callback.Dispose();
            return true;
        }
        var path = DownloadPaths.Unique(DownloadPaths.DownloadsFolder(), downloadItem.SuggestedFileName);
        using (callback) callback.Continue(path, showDialog: false);
        var (id, url) = (downloadItem.Id, downloadItem.OriginalUrl);
        dispatcher.InvokeAsync(() =>
        {
            downloads.Started(id, path, Path.GetFileName(path), SourceHost(url));
            onStarted(chromiumWebBrowser, url);
        });
        return true;
    }

    protected override void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
        IDownloadItemCallback callback)
    {
        var (id, received, total, speed) = (downloadItem.Id, downloadItem.ReceivedBytes, downloadItem.TotalBytes, downloadItem.CurrentSpeed);
        var (complete, cancelled) = (downloadItem.IsComplete, downloadItem.IsCancelled || downloadItem.IsInterrupted);
        if (complete || cancelled) _callbacks.TryRemove(id, out _);
        else _callbacks[id] = callback;
        dispatcher.InvokeAsync(() => downloads.Update(id, received, total, speed, complete, cancelled));
    }

    // O anfitrião de onde vem a transferência, para a pergunta de «abrir um programa»; null se o endereço não o tem.
    internal static string? SourceHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    public void Cancel(int id)
    {
        if (_callbacks.TryRemove(id, out var callback) && !callback.IsDisposed) callback.Cancel();
    }
}
