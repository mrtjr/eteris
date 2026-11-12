using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;

namespace Eteris.Browser;

// «Apagar dados de navegação» (eteris://settings/clear): o motor apaga no contexto da janela que fez o pedido, e a
// resposta só sai depois, a dizer o que falhou. O IBrowser do pedido só vale durante a chamada da rota: aqui procura-se
// a janela pelo identificador e usa-se o gestor de cookies do contexto e uma página viva dela.
public partial class MainWindow
{
    // Chamado fora do fio da interface. Sem janela (fechada entretanto), falha o que foi pedido. historyOrigins: as
    // origens do histórico do perfil, recolhidas antes de o apagar (vazia numa janela privada).
    internal static Task<ClearOutcome> ClearEngineDataAsync(int browserId, ClearDataRequest request, IReadOnlyList<string> historyOrigins)
        => System.Windows.Application.Current.Dispatcher.InvokeAsync(() => WindowOf(browserId) is { } window
            ? window.ClearEngineDataAsync(request, historyOrigins)
            : Task.FromResult(new ClearOutcome(request.Cookies, request.Cache))).Task.Unwrap();

    private async Task<ClearOutcome> ClearEngineDataAsync(ClearDataRequest request, IReadOnlyList<string> historyOrigins)
    {
        var cookiesFailed = false;
        var cacheFailed = false;
        var storageFailed = 0;
        var cookieSitesUnknown = false;
        // Cada resposta leva também os sites que ficaram por conhecer (os cookies não se deixaram ler).
        ClearOutcome Done(ClearOutcome outcome) => outcome with { CookieSitesUnknown = cookieSitesUnknown };
        IReadOnlyList<string> origins = [];
        if (request.Cookies)
        {
            var manager = CookieContext.GetCookieManager(null);
            // «Cookies e dados de sites»: o armazenamento (local, IndexedDB, cache e trabalhadores) apaga-se por origem —
            // as do histórico, as dos cookies (lidas antes de os apagar) e as das abas abertas deste contexto.
            IEnumerable<string> domains;
            try { domains = (await manager.VisitAllCookiesAsync()).Select(c => c.Domain).ToList(); }
            catch (Exception) { domains = []; cookieSitesUnknown = true; }
            origins = SiteScope.KnownOrigins(historyOrigins, domains, IsPrivate ? Tabs.Tabs.Select(t => t.Address) : OpenAddresses());
            // O gestor do contexto da janela não precisa de página; o CefSharp devolve -1 quando não chega aos cookies.
            try { cookiesFailed = await manager.DeleteCookiesAsync(string.Empty, string.Empty) < 0; }
            catch (Exception) { cookiesFailed = true; }
        }
        if (origins.Count == 0 && !request.Cache) return Done(new ClearOutcome(cookiesFailed));

        // O armazenamento e a cache apagam-se pelo DevTools de uma página viva deste contexto.
        if (await LiveBrowserAsync(TimeSpan.FromSeconds(8)) is not { } browser)
            return Done(new ClearOutcome(cookiesFailed, request.Cache, origins.Count));
        try
        {
            using var client = browser.GetDevToolsClient();
            foreach (var origin in origins)
            {
                // Uma origem recusada pelo motor não impede as outras.
                try { await DevToolsCalls.Run(() => client.Storage.ClearDataForOriginAsync(origin, "all")); }
                catch (Exception) { storageFailed++; }
            }
            if (request.Cache)
            {
                try { await DevToolsCalls.Run(() => client.Network.ClearBrowserCacheAsync()); }
                catch (Exception) { cacheFailed = true; }
            }
        }
        catch (Exception) { return Done(new ClearOutcome(cookiesFailed, request.Cache, origins.Count)); }
        return Done(new ClearOutcome(cookiesFailed, cacheFailed, storageFailed));
    }
}
