using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    // Página que foi abaixo: mostra o aviso (com «Tentar novamente») no lugar do ecrã vazio.
    private void OnPageCrashed(BrowserTab tab)
    {
        if (!_browsers.TryGetValue(tab, out var browser) || browser.IsDisposed) return;
        tab.IsLoading = false;
        var failed = ErrorPage.ReloadTarget(tab.Address) ?? tab.Address;
        browser.Load(ErrorPage.Url(ErrorPage.RendererCrashed, failed));
    }
}
