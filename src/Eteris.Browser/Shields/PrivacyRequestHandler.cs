using CefSharp;

namespace Eteris.Browser.Shields;

// Uma navegação da página principal, para seguir as cadeias de saltos (Shields/BounceTracking.cs). «Rewritten»:
// foi o próprio Éteris a trocar o endereço antes de o pedido sair (salto, limpeza, HTTPS).
public sealed record MainNavigation(string? CurrentUrl, string Url, bool UserGesture, bool IsRedirect, TransitionType Transition, bool Rewritten);


public sealed class PrivacyRequestHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Func<IBrowser, string, Task> onNavigate, Action<string> onBlocked,
    Action? onCrashed = null, string? userAgent = null, bool isPrivate = false,
    Func<string, bool>? localNetworkAllowed = null, Action<string, string>? onLocalNetworkBlocked = null,
    Action<IReadOnlyList<string>>? onTrackingSkipped = null, Action<MainNavigation>? onMainNavigation = null,
    Action<string, string>? onCloaked = null, SiteObservatory? observatory = null)
    : CefSharp.Handler.RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
        bool userGesture, bool isRedirect)
    {
        if (onMainNavigation is not null && frame.IsMain)
            onMainNavigation(new MainNavigation(browser.MainFrame?.Url, request.Url, userGesture, isRedirect, request.TransitionType,
                _resources.TakeRewrite(browser.Identifier, request.Url)));
        return false;
    }

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
        localNetworkAllowed, onLocalNetworkBlocked, onTrackingSkipped, onCloaked, observatory);

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
        string? userAgent, bool isPrivate, Func<string, bool>? localNetworkAllowed, Action<string, string>? onLocalNetworkBlocked,
        Action<IReadOnlyList<string>>? onTrackingSkipped, Action<string, string>? onCloaked, SiteObservatory? observatory)
        : CefSharp.Handler.ResourceRequestHandler
    {
        // Observatório do site (Shields/SiteObservatory.cs): cada sub-recurso com a decisão que teve. Um incremento num
        // dicionário por pedido — o resto só se calcula quando o painel abre.
        private void Observe(string url, ResourceType type, ObservedVerdict verdict, string? detail = null)
            => observatory?.Record(url, type, verdict, detail);

        // Desmascarar CNAME (Shields/CnameUncloak.cs) para um pedido que as listas deixaram passar. Com o nome já na
        // cache decide-se logo; senão a consulta corre fora da thread de rede e o pedido espera no máximo
        // CnameUncloak.MaxWait (IRequestCallback) — passado isso segue, e os seguintes já encontram a resposta.
        private CefReturnValue Uncloak(IBrowser browser, IRequest request, string? pageUrl, string? documentUrl, string type,
            IRequestCallback callback)
        {
            if (!Data.Settings.Current.UncloakCname || !CnameUncloak.IsCandidate(pageUrl, request.Url, request.ResourceType)
                || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                Observe(request.Url, request.ResourceType, ObservedVerdict.Allowed);
                return CefReturnValue.Continue;
            }
            var resourceType = request.ResourceType;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var host = uri.IdnHost.ToLowerInvariant();
            var url = request.Url;
            var engine = blocker();
            if (Cname.Cache.TryGet(host, out var chain))
            {
                var target = CnameUncloak.CloakedTracker(url, documentUrl, type, chain, engine.ShouldBlock);
                CnameStats.Decision(System.Diagnostics.Stopwatch.GetTimestamp() - started);
                Observe(url, resourceType, target is null ? ObservedVerdict.Allowed : ObservedVerdict.Cloaked, target);
                if (target is null) return CefReturnValue.Continue;
                Cloaked(uri.Host, target);
                return CefReturnValue.Cancel;
            }
            if (!Cname.Quota.TryTake(browser.Identifier, pageUrl))
            {
                CnameStats.Decision(System.Diagnostics.Stopwatch.GetTimestamp() - started);
                Observe(url, resourceType, ObservedVerdict.Allowed);
                return CefReturnValue.Continue;
            }
            var lookup = Cname.Cache.LookupAsync(host);
            CnameStats.Decision(System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Task.WhenAny(lookup, Task.Delay(CnameUncloak.MaxWait)).ContinueWith(_ =>
            {
                var answered = lookup.IsCompletedSuccessfully;
                CnameStats.Waited(System.Diagnostics.Stopwatch.GetTimestamp() - started, timedOut: !answered);
                var target = answered ? CnameUncloak.CloakedTracker(url, documentUrl, type, lookup.Result, engine.ShouldBlock) : null;
                Observe(url, resourceType, target is null ? ObservedVerdict.Allowed : ObservedVerdict.Cloaked, target);
                if (target is not null) Cloaked(uri.Host, target);
                using (callback) callback.Continue(target is null);
            }, TaskScheduler.Default);
            return CefReturnValue.ContinueAsync;
        }

        private void Cloaked(string host, string target)
        {
            CnameStats.Blocked();
            if (onCloaked is not null) onCloaked(host, target);
            else onBlocked(CnameUncloak.Label(host, target));
        }

        // Saltos de rastreio evitados na navegação em curso, por browser: só se contam quando a página de destino
        // começa a carregar (depois do «limpar o Escudo» dessa navegação), senão a contagem perdia-se logo.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime At, List<string> Hosts)> _skips = new();

        // Último endereço da página principal reescrito antes de sair, por browser (ver MainNavigation.Rewritten).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _rewrites = new();

        private void Rewrite(IBrowser browser, IRequest request, string url)
        {
            _rewrites[browser.Identifier] = url;
            request.Url = url;
        }

        public bool TakeRewrite(int browserId, string url)
            => _rewrites.TryGetValue(browserId, out var rewritten) && rewritten == url && _rewrites.TryRemove(browserId, out _);

        private static string? Jump(string url, Func<string, bool> isProtected)
        {
            if (!Data.Settings.Current.SkipTrackingRedirects) return null;
            // Com o Escudo desligado no site do salto (o utilizador quis), o pedido segue como está.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var from) || !isProtected(from.Host)) return null;
            return Debounce.Destination(url, TrackerBlocker.Default.Lists);
        }

        private void RememberSkip(IBrowser browser, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var from)) return;
            var host = from.Host.ToLowerInvariant();
            _skips.AddOrUpdate(browser.Identifier, _ => (DateTime.UtcNow, [host]), (_, old) =>
                DateTime.UtcNow - old.At > TimeSpan.FromSeconds(10) ? (DateTime.UtcNow, [host]) : (DateTime.UtcNow, [.. old.Hosts, host]));
        }

        private void ReportSkips(IBrowser browser)
        {
            if (!_skips.TryRemove(browser.Identifier, out var pending) || DateTime.UtcNow - pending.At > TimeSpan.FromSeconds(10)) return;
            onTrackingSkipped?.Invoke(pending.Hosts);
        }

        // Um redirecionamento do servidor para um salto conhecido (um encurtador que manda para l.facebook.com/l.php?u=…)
        // também vai direto ao destino.
        protected override void OnResourceRedirect(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            IResponse response, ref string newUrl)
        {
            if (request.ResourceType != ResourceType.MainFrame || Jump(newUrl, isProtected) is not { } jump) return;
            RememberSkip(browser, newUrl);
            newUrl = jump;
        }

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
                // Salto de rastreio com o destino no endereço (Shields/Debounce.cs): vai-se direto ao destino.
                if (Jump(request.Url, isProtected) is { } jump)
                {
                    RememberSkip(browser, request.Url);
                    Rewrite(browser, request, jump);
                    return CefReturnValue.Continue;
                }
                // Links sem parâmetros de rastreio (utm_*, fbclid…): o pedido sai já limpo.
                if (Data.Settings.Current.CleanUrls && UrlCleaner.Clean(request.Url) is var clean && clean != request.Url)
                {
                    Rewrite(browser, request, clean);
                    return CefReturnValue.Continue;
                }
                if (Data.Settings.Current.HttpsFirst && ShieldEngine.Https.Upgrade(request.Url, isPrivate) is { } secure)
                {
                    Rewrite(browser, request, secure);
                    return CefReturnValue.Continue;
                }
                // O pedido da página só sai depois de os scriptlets estarem registados (máx. 1 s):
                // sem isto o documento podia nascer antes do registo e o anti-bloqueador ganhava a corrida.
                observatory?.Reset(request.Url);
                var ready = onNavigate(browser, request.Url);
                ReportSkips(browser);
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
                Observe(request.Url, request.ResourceType, ObservedVerdict.LocalNetwork);
                return CefReturnValue.Cancel;
            }
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && !isProtected(page.Host))
            {
                Observe(request.Url, request.ResourceType, ObservedVerdict.ShieldOff);
                return CefReturnValue.Continue;
            }

            // Modo rigoroso neste site: scripts e frames de outros domínios não carregam (além das listas).
            if (pageUrl is not null && Uri.TryCreate(pageUrl, UriKind.Absolute, out var top) && Data.AppData.IsStrict(top.Host)
                && StrictPolicy.ShouldBlock(pageUrl, request.Url, request.ResourceType))
            {
                onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var strictBlocked) ? strictBlocked.Host : request.Url);
                Observe(request.Url, request.ResourceType, ObservedVerdict.Strict);
                return CefReturnValue.Cancel;
            }

            // As opções $domain e $third-party das listas referem-se ao documento que fez o pedido (pode ser um iframe).
            var documentUrl = frame?.Url is { Length: > 0 } frameUrl && frameUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? frameUrl
                : pageUrl;
            var adblockType = ResourceTypes.ToAdblock(request.ResourceType);
            if (!blocker().ShouldBlock(request.Url, documentUrl, adblockType))
                return Uncloak(browser, request, pageUrl, documentUrl, adblockType, callback);

            onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var blocked) ? blocked.Host : request.Url);
            Observe(request.Url, request.ResourceType, ObservedVerdict.Blocked);
            return CefReturnValue.Cancel;
        }
    }
}
