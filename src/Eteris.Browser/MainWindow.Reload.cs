using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

public partial class MainWindow
{
    // Numa página de erro, recarregar volta a tentar o endereço que falhou (não a própria página de erro).
    // Antes, os scripts do site são atualizados (o escudo ou o modo rigoroso podem ter mudado neste site).
    private async void ReloadActive(bool ignoreCache = false)
    {
        if (ReloadVideo()) return;
        if (Tabs.Active is not { } tab || ActiveBrowser is not { } browser) return;
        // Recarregar volta a perguntar pela rede local (um «Bloquear» por engano não fica sem saída nesta janela).
        if (Shields.LocalNetwork.SiteOf(tab.Address) is { } site) _localNetworkAsked.Remove(site);
        await ScriptsReadyFor(tab, browser, tab.Address);
        if (browser.IsDisposed) return;
        if (ErrorPage.IsErrorPage(browser.Address ?? string.Empty))
        {
            browser.Load(tab.Address);
            return;
        }
        browser.Reload(ignoreCache);
    }
}
