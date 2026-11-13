using CefSharp;

namespace Eteris.Browser.Browser;

// A cache de memória do renderer de uma página (a MemoryCache do Blink), esvaziada quando um site fica mais protegido
// (Shields/ShieldTightening.cs). O Network.setCacheDisabled(true) do DevTools chama MemoryCache::EvictResources no
// processo da página (inspector_network_agent.cc); o false logo a seguir repõe a cache, sempre (finally): uma falha a
// meio não deixa a página sem cache. Uma sessão própria e curta, não a partilhada do PageScript. A cache em disco não se
// toca: um acerto nela passa pelo tratador do Escudo. Medido no Público: depois disto o recarregar e a visita nova
// voltam a pedir o gpt.js, e o Escudo bloqueia-o.
public static class RendererCache
{
    public static async Task EvictAsync(IWebBrowser browser)
    {
        if (browser.IsDisposed || !browser.IsBrowserInitialized) return;
        // Braço «puro» das medições de desempenho (só com ETERIS_TEST_PURO): nenhuma sessão DevTools.
        if (Infrastructure.PerfDiagnostics.Without(Infrastructure.PerfLayers.DevTools)) return;
        try
        {
            using var client = browser.GetDevToolsClient();
            await DevToolsCalls.Run(() => client.Network.EnableAsync());
            try { await DevToolsCalls.Run(() => client.Network.SetCacheDisabledAsync(true)); }
            finally { await DevToolsCalls.Run(() => client.Network.SetCacheDisabledAsync(false)); }
        }
        catch (Exception)
        {
            // A página fechou entretanto: não há cache para esvaziar.
        }
    }
}
