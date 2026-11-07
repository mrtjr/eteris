using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Shields;

namespace Eteris.Browser;

// O documento principal falhou (abas e sites fixados da barra lateral): a página de erro, o aviso do «HTTPS
// primeiro», ou — quando a falha veio de um passo do histórico a partir da própria página de erro — mais um passo
// para trás (ErrorPage.SkipsBack).
public partial class MainWindow
{
    // A última navegação do documento principal de um motor (vem de OnBeforeBrowse, na thread do CEF) e os saltos
    // seguidos, com um limite (se o Chromium não chegasse a registar a entrada que falhou, voltar atrás repeti-la-ia).
    private sealed class MainFrameHistory
    {
        public MainNavigation? Last;
        public int Skips;
        public DateTime SkippedAt;
    }

    private const int MaxHistorySkips = 4;

    // Corre na thread do CEF (LoadError).
    private void RecoverMainFrame(ChromiumWebBrowser browser, LoadErrorEventArgs e, bool isPrivate, MainFrameHistory history)
    {
        if (!e.Frame.IsMain) return;
        var failed = e.FailedUrl;
        var nav = Volatile.Read(ref history.Last);
        var historyStep = nav is not null && (nav.Transition & TransitionType.ForwardBack) != 0 && ErrorPage.SameAddress(nav.Url, failed);
        var now = DateTime.UtcNow;
        var afterSkip = now - history.SkippedAt < TimeSpan.FromSeconds(5);
        if (!afterSkip) history.Skips = 0;
        if (ErrorPage.SkipsBack(historyStep, nav?.CurrentUrl, failed, afterSkip) && e.Browser.CanGoBack && history.Skips < MaxHistorySkips)
        {
            history.Skips++;
            history.SkippedAt = now;
            Dispatcher.InvokeAsync(() =>
            {
                if (!browser.IsDisposed) browser.Back();
            });
            return;
        }
        var target = ErrorPage.Recovery((int)e.ErrorCode, failed, url => ShieldEngine.Https.FailedUpgrade(url, isPrivate),
            url => ShieldEngine.Https.IsAllowedUrl(url, isPrivate));
        if (target is not null) Dispatcher.InvokeAsync(() =>
        {
            if (!browser.IsDisposed) browser.Load(target);
        });
    }
}
