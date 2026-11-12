using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

// «Apagar dados de navegação» (eteris://settings/clear): o motor apaga no contexto da janela que fez o pedido, e a
// resposta só sai depois, a dizer o que falhou. O IBrowser do pedido só vale durante a chamada da rota: aqui procura-se
// a janela pelo identificador e usa-se o gestor de cookies do contexto e uma página viva dela.
public partial class MainWindow
{
    // Chamado fora do fio da interface. Sem janela (fechada entretanto), falha o que foi pedido.
    internal static Task<ClearOutcome> ClearEngineDataAsync(int browserId, ClearDataRequest request)
        => System.Windows.Application.Current.Dispatcher.InvokeAsync(() => WindowOf(browserId) is { } window
            ? window.ClearEngineDataAsync(request)
            : Task.FromResult(new ClearOutcome(request.Cookies, request.Cache))).Task.Unwrap();

    private async Task<ClearOutcome> ClearEngineDataAsync(ClearDataRequest request)
    {
        var cookiesFailed = false;
        var cacheFailed = false;
        if (request.Cookies)
        {
            // O gestor do contexto da janela não precisa de página; o CefSharp devolve -1 quando não chega aos cookies.
            try { cookiesFailed = await CookieContext.GetCookieManager(null).DeleteCookiesAsync(string.Empty, string.Empty) < 0; }
            catch (Exception) { cookiesFailed = true; }
        }
        if (request.Cache)
        {
            // A cache apaga-se pelo DevTools de uma página viva deste contexto.
            if (await LiveBrowserAsync(TimeSpan.FromSeconds(8)) is not { } browser) cacheFailed = true;
            else
            {
                try
                {
                    using var client = browser.GetDevToolsClient();
                    await DevToolsCalls.Run(() => client.Network.ClearBrowserCacheAsync());
                }
                catch (Exception) { cacheFailed = true; }
            }
        }
        return new ClearOutcome(cookiesFailed, cacheFailed);
    }
}
