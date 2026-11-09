using CefSharp;

namespace Eteris.Browser.Shields;

// Pedidos sem aba: o service worker e o shared worker de uma página (e o fetch com que um service worker reencaminha os
// pedidos da própria página, respondWith(fetch(e.request))), o service worker de uma extensão, pedidos que ficam sem
// a aba que os fez. A CEF não os entrega ao PrivacyRequestHandler de nenhuma aba (não há browser): pergunta ao
// tratador do contexto de pedidos. Sem ele, iam para os rastreadores sem listas, sem GPC, sem filtro de cookies de
// terceiros nem bloqueio da rede local (prova ao vivo em docs/paridade.md, secção 8). Aqui decidem-se pelo mesmo
// PrivacyResourceHandler das abas, com a página de topo deduzida de quem fez o pedido (a origem do worker).
// Só a CEF chama isto, e só quando nenhuma aba respondeu: os pedidos das abas não passam aqui (nenhum custo para eles).
public sealed class BrowserlessRequestHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, bool isPrivate,
    Func<string, bool>? localNetworkAllowed, Action<string?, string> onBlocked) : CefSharp.Handler.RequestContextHandler
{
    // Um tratador por página de topo (o que bloquear conta para esse site); poucos sites têm workers ao mesmo tempo.
    internal const int MaxTops = 256;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PrivacyRequestHandler.PrivacyResourceHandler> _byTop =
        new(StringComparer.Ordinal);

    // O browser e o frame que o CefSharp passa aqui embrulham ponteiros que podem ser nulos (um service worker não tem
    // browser) e qualquer leitura deles fechava o Éteris: não se tocam. Com browser (um sem RequestHandler, como as
    // DevTools), o tratador ignora-o e decide pela origem de quem pediu, como para um worker.
    protected override IResourceRequestHandler? GetResourceRequestHandler(IBrowser browser, IFrame frame, IRequest request,
        bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
    {
        try
        {
            string? referrer = null;
            if (string.IsNullOrEmpty(requestInitiator))
                try { referrer = request.ReferrerUrl; }
                catch (Exception) { }
            return For(TopOf(requestInitiator, referrer));
        }
        // Sem decisão possível, o pedido segue como antes desta correção (o motor não fica sem rede).
        catch (Exception) { return null; }
    }

    internal PrivacyRequestHandler.PrivacyResourceHandler For(string? top)
    {
        var key = top ?? string.Empty;
        if (_byTop.TryGetValue(key, out var handler)) return handler;
        if (_byTop.Count >= MaxTops) _byTop.Clear();
        return _byTop.GetOrAdd(key, _ => new PrivacyRequestHandler.PrivacyResourceHandler(blocker, isProtected,
            host => onBlocked(top, host), userAgent: null, isPrivate: isPrivate, localNetworkAllowed: localNetworkAllowed,
            browserless: true, browserlessTop: top));
    }

    internal int Tops => _byTop.Count;

    // A página de topo de um pedido sem aba: a origem de quem o fez (o worker, a extensão), como documento «/». Uma origem
    // opaca («null»: iframe com sandbox, worker de data:) não é o site de ninguém. Sem origem nenhuma (pedido do próprio
    // navegador), vale a do referrer http(s), se houver. null: nenhum site (os cookies de terceiros não passam, a rede
    // local fica fechada, as listas decidem sem documento).
    internal static string? TopOf(string? initiator, string? referrer)
    {
        if (!string.IsNullOrEmpty(initiator))
            return initiator != "null" && Uri.TryCreate(initiator, UriKind.Absolute, out var origin) && origin.Host.Length > 0
                ? origin.GetLeftPart(UriPartial.Authority) + "/"
                : null;
        return Uri.TryCreate(referrer, UriKind.Absolute, out var from) && from.Scheme is "http" or "https" && from.Host.Length > 0
            ? from.GetLeftPart(UriPartial.Authority) + "/"
            : null;
    }
}

// O tratador do contexto do perfil (o global do motor) e a contagem do que ele bloqueia.
public static class BrowserlessRequests
{
    private static IRequestContext? _profile;

    // Um bloqueio sem aba no perfil: (página de topo, anfitrião). A janela conta-o na aba que mostra esse site.
    public static event Action<string?, string>? Blocked;

    // Os tratadores de contexto (este e o de cada janela privada, MainWindow.Private.cs): o mesmo Escudo das abas, e
    // cada bloqueio conta no total do Escudo como o de uma aba (o relatório de privacidade conta-o no Observe).
    public static BrowserlessRequestHandler Handler(bool isPrivate, Func<string, bool>? localNetworkAllowed, Action<string?, string>? onBlocked)
        => new(() => ShieldEngine.Current, host => Data.AppData.IsShieldOn(host, isPrivate), isPrivate, localNetworkAllowed, (top, host) =>
        {
            ShieldStats.CountBlocked(persist: !isPrivate);
            onBlocked?.Invoke(top, host);
        });

    // O contexto global do motor não aceita tratador depois de criado: um contexto que partilha o armazenamento com ele
    // e tem o tratador põe-no no perfil. A CEF entrega-lhe os pedidos dos workers do perfil (são do perfil e não têm
    // aba; ver CefBrowserContext::UpdateWorkerRequestContextHandler). Vive até ao fecho do motor (Release).
    public static void Install()
    {
        if (_profile is not null) return;
        _profile = new RequestContext(Cef.GetGlobalRequestContext(), Handler(isPrivate: false,
            site => LocalNetworkGrants.IsAllowed(site, false) || Data.AppData.SitePermission(site, Permissions.SitePermission.LocalNetwork) == true,
            (top, host) => Blocked?.Invoke(top, host)));
    }

    public static void Release()
    {
        _profile?.Dispose();
        _profile = null;
    }
}
