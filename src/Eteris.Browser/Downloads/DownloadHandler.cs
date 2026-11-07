using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using CefSharp;

namespace Eteris.Browser.Downloads;

// Uma instância para todas as abas: transfere sempre para a pasta Transferências, sem diálogo. divert pode
// recusar uma transferência antes de começar (o Alt+clique nativo do Chromium, que o Éteris usa para espreitar).
public sealed class DownloadHandler(DownloadsViewModel downloads, Dispatcher dispatcher, Action<IWebBrowser, string> onStarted,
    Func<IWebBrowser, string, bool>? divert = null)
    : CefSharp.Handler.DownloadHandler
{
    private readonly ConcurrentDictionary<int, IDownloadItemCallback> _callbacks = new();

    protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
        => divert?.Invoke(chromiumWebBrowser, url) != true;

    protected override bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem,
        IBeforeDownloadCallback callback)
    {
        var path = DownloadPaths.Unique(DownloadPaths.DownloadsFolder(), downloadItem.SuggestedFileName);
        using (callback) callback.Continue(path, showDialog: false);
        var (id, url) = (downloadItem.Id, downloadItem.OriginalUrl);
        dispatcher.InvokeAsync(() =>
        {
            downloads.Started(id, path, Path.GetFileName(path));
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

    public void Cancel(int id)
    {
        if (_callbacks.TryRemove(id, out var callback) && !callback.IsDisposed) callback.Cancel();
    }
}
