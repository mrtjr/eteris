using CefSharp;

namespace Eteris.Browser.Shields;

public sealed class PrivacyRequestHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Func<IBrowser, string, Task> onNavigate, Action<string> onBlocked,
    Action? onCrashed = null, string? userAgent = null, bool isPrivate = false,
    Func<string, bool>? localNetworkAllowed = null, Action<string, string>? onLocalNetworkBlocked = null)
    : CefSharp.Handler.RequestHandler
{
    // Cabeçalhos de um pedido feito «como telemóvel» (vista móvel de um site fixado na barra lateral): o agente e
    // as dicas de cliente que o acompanham, para o servidor não ver um Android a dizer-se Windows.
    public static IReadOnlyList<(string Name, string Value)> MobileHeaders(string userAgent) =>
    [
        ("User-Agent", userAgent),
        ("Sec-CH-UA-Mobile", "?1"),
        ("Sec-CH-UA-Platform", "\"Android\""),
    ];

    // Tira os cabeçalhos de dicas do cliente que identificam o computador (pedidas pelo site com Accept-CH).
    private static void StripIdentifyingHints(IRequest request)
    {
        var headers = request.Headers;
        var names = headers.AllKeys.Where(k => k is not null && RequestIdentity.IsIdentifyingHint(k)).ToList();
        if (names.Count == 0) return;
        foreach (var name in names) headers.Remove(name);
        request.Headers = headers;
    }

    // O processo da página morreu (crash, falta de memória): a janela mostra a página de erro em vez de um ecrã vazio.
    protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status,
        int errorCode, string errorMessage)
        => onCrashed?.Invoke();

    private readonly PrivacyResourceHandler _resources = new(blocker, isProtected, onNavigate, onBlocked, userAgent, isPrivate,
        localNetworkAllowed, onLocalNetworkBlocked);

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => _resources;

    // Com o Éteris a tratar cada pedido, é o CEF que lê e grava os cookies dos pedidos — e o bloqueio de cookies de
    // terceiros do Chromium não chegava lá: um pixel de outro site embutido numa página levava os cookies desse site.
    // Este filtro recusa enviar e gravar cookies de outro site que não o de topo (ThirdPartyCookies.Allow).
    private sealed class ThirdPartyCookieFilter(Func<string, bool> isProtected) : ICookieAccessFilter
    {
        public bool CanSendCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, Cookie cookie)
            => Allow(browser, request);

        public bool CanSaveCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IResponse response, Cookie cookie)
            => Allow(browser, request);

        private bool Allow(IBrowser browser, IRequest request)
        {
            var top = browser?.MainFrame?.Url;
            return ThirdPartyCookies.Allow(top, request.Url, request.ResourceType, Data.Settings.Current.BlockThirdPartyCookies,
                top is not null && Uri.TryCreate(top, UriKind.Absolute, out var page) && !isProtected(page.Host));
        }
    }

    private sealed class PrivacyResourceHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Func<IBrowser, string, Task> onNavigate, Action<string> onBlocked,
        string? userAgent, bool isPrivate, Func<string, bool>? localNetworkAllowed, Action<string, string>? onLocalNetworkBlocked)
        : CefSharp.Handler.ResourceRequestHandler
    {
        protected override ICookieAccessFilter GetCookieAccessFilter(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
            => _cookies;

        private readonly ThirdPartyCookieFilter _cookies = new(isProtected);

        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, IRequestCallback callback)
        {
            foreach (var (name, value) in PrivacySignals.Headers) request.SetHeaderByName(name, value, true);
            // Uma só língua (a com que o motor arrancou) e só as dicas do cliente de baixa entropia, mesmo que o site peça
            // mais (Shields/RequestIdentity.cs). Sem reescrever o cabeçalho, o Chromium juntava a língua-base com peso.
            if (EngineHardening.StartedWithLanguage is { Length: > 0 } language) request.SetHeaderByName("Accept-Language", language, true);
            StripIdentifyingHints(request);
            // Referrer entre sites só com a origem (Shields/ReferrerCap.cs), quando o escudo está ligado no site que o envia.
            if (ReferrerCap.Decide(request.ReferrerUrl, request.Url) is { Kind: not ReferrerCap.Kind.Keep } cap
                && Uri.TryCreate(request.ReferrerUrl, UriKind.Absolute, out var referrer) && isProtected(referrer.Host))
                request.SetReferrer(cap.Value ?? string.Empty, cap.Value is null ? ReferrerPolicy.NoReferrer : ReferrerPolicy.Origin);
            if (userAgent is not null)
                foreach (var (name, value) in MobileHeaders(userAgent)) request.SetHeaderByName(name, value, true);

            if (request.ResourceType == ResourceType.MainFrame)
            {
                // Links sem parâmetros de rastreio (utm_*, fbclid…): o pedido sai já limpo.
                if (Data.Settings.Current.CleanUrls && UrlCleaner.Clean(request.Url) is var clean && clean != request.Url)
                {
                    request.Url = clean;
                    return CefReturnValue.Continue;
                }
                if (Data.Settings.Current.HttpsFirst && ShieldEngine.Https.Upgrade(request.Url, isPrivate) is { } secure)
                {
                    request.Url = secure;
                    return CefReturnValue.Continue;
                }
                // O pedido da página só sai depois de os scriptlets estarem registados (máx. 1 s):
                // sem isto o documento podia nascer antes do registo e o anti-bloqueador ganhava a corrida.
                var ready = onNavigate(browser, request.Url);
                if (ready.IsCompleted) return CefReturnValue.Continue;
                Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(1))).ContinueWith(_ =>
                {
                    using (callback) callback.Continue(true);
                }, TaskScheduler.Default);
                return CefReturnValue.ContinueAsync;
            }

            var pageUrl = browser.MainFrame?.Url;
            // Rede local (Shields/LocalNetwork.cs): uma página pública não chega a este computador nem à rede de casa sem
            // autorização do utilizador para o site — com ou sem escudo, é uma proteção e não um bloqueio de anúncios.
            if (LocalNetwork.Decide(pageUrl, request.Url, request.ResourceType, allowedForSite: false) == LocalNetwork.Decision.Block
                && LocalNetwork.SiteOf(pageUrl) is { } site && localNetworkAllowed?.Invoke(site) != true)
            {
                onLocalNetworkBlocked?.Invoke(site, request.Url);
                return CefReturnValue.Cancel;
            }
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && !isProtected(page.Host))
                return CefReturnValue.Continue;

            // Modo rigoroso neste site: scripts e frames de outros domínios não carregam (além das listas).
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var top) && Data.AppData.IsStrict(top.Host)
                && StrictPolicy.ShouldBlock(pageUrl, request.Url, request.ResourceType))
            {
                onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var strictBlocked) ? strictBlocked.Host : request.Url);
                return CefReturnValue.Cancel;
            }

            // As opções $domain e $third-party das listas referem-se ao documento que fez o pedido (pode ser um iframe).
            var documentUrl = frame?.Url is { Length: > 0 } frameUrl && frameUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? frameUrl
                : pageUrl;
            if (!blocker().ShouldBlock(request.Url, documentUrl, ResourceTypes.ToAdblock(request.ResourceType)))
                return CefReturnValue.Continue;

            onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var blocked) ? blocked.Host : request.Url);
            return CefReturnValue.Cancel;
        }
    }
}
