namespace Eteris.Browser.Shields;

// Mais proteção num site: a exceção do Escudo sai (o interruptor, a paleta, «Repor site», a sincronização) ou o modo
// rigoroso liga. O que o site carregou com menos proteção não pode voltar a correr. O tratador do Escudo decide cada
// pedido ao vivo (PrivacyRequestHandler), mas o Blink guarda na cache de memória do renderer os scripts frescos e
// serve-os ao documento seguinte do mesmo processo sem pedido nenhum ao processo do navegador — sem passar pelo
// tratador. Medido ao vivo na 0.2.17 no Público (o gpt.js tem private, max-age=900; o pubads_impl, immutable de um ano):
// com a exceção desligada, o recarregar automático, o recarregar seguinte e uma visita nova na mesma aba corriam o
// gpt.js da cache de memória (requestServedFromCache, transferSize 0, googletag.apiReady true), também na outra aba do
// site. A cache em disco não: um acerto nela passa pelo tratador e é bloqueado (depois de reiniciar, o gpt.js volta a
// ser bloqueado); uma aba nova também não (outro processo). Por isso as janelas ouvem Tightened e esvaziam a cache de
// memória das páginas abertas do site (Browser/RendererCache.cs), e a aba ativa recarrega sem cache.
public static class ShieldTightening
{
    // O anfitrião cujo site ficou mais protegido; pode chegar de qualquer thread (a sincronização aplica fora da janela).
    public static event Action<string>? Tightened;

    // Quando cada site ficou mais protegido pela última vez (Environment.TickCount64), nesta sessão.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> s_tightenedAt = new(StringComparer.Ordinal);

    // Quem muda a proteção de um site chama isto: só uma mudança real para mais proteção avisa.
    public static void Changed(string host, bool wasProtected, bool isProtected)
    {
        if (!isProtected || wasProtected) return;
        s_tightenedAt[SiteOf(host)] = Environment.TickCount64;
        Tightened?.Invoke(host);
    }

    // A página está no site (o mesmo domínio registável, como a exceção do Escudo: Data/ShieldExceptionStore.cs).
    public static bool Affects(string host, string? pageUrl)
        => PageSite(pageUrl) is { } site && site == SiteOf(host);

    // Voltar/Avançar pela cache de páginas repõe o documento tal como estava, sem pedidos (medido: o gpt.js da reprodução
    // local corria com googletag.apiReady true). Se o site dele ficou mais protegido depois de ele carregar (loadedAt;
    // null = a aba não o guardou), mesmo que tenha sido deixado só depois disso, a página tem de carregar outra vez.
    public static bool TightenedSince(string? pageUrl, long? loadedAt)
        => PageSite(pageUrl) is { } site && s_tightenedAt.TryGetValue(site, out var at) && (loadedAt is not { } loaded || at >= loaded);

    private static string SiteOf(string host) => TrackerBlocker.RegistrableDomain(host.ToLowerInvariant());

    private static string? PageSite(string? pageUrl)
        => Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? SiteOf(uri.Host)
            : null;
}
