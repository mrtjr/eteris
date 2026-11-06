using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

public partial class MainWindow
{
    // Numa página de erro, recarregar volta a tentar o endereço que falhou (não a própria página de erro).
    private void ReloadActive(bool ignoreCache = false)
    {
        if (ReloadVideo()) return;
        if (Tabs.Active is not { } tab || ActiveBrowser is not { } browser) return;
        if (ErrorPage.IsErrorPage(browser.Address ?? string.Empty))
        {
            browser.Load(tab.Address);
            return;
        }
        browser.Reload(ignoreCache);
    }
}
