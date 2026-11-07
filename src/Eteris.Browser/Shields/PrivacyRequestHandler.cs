using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Shields;

// Uma navegação da página principal, para seguir as cadeias de saltos (Shields/BounceTracking.cs). «Rewritten»:
// foi o próprio Éteris a trocar o endereço antes de o pedido sair (salto, limpeza, HTTPS).
public sealed record MainNavigation(string? CurrentUrl, string Url, bool UserGesture, bool IsRedirect, TransitionType Transition, bool Rewritten);

// onMainFrameRequest: começou o pedido de uma página nova neste separador (não espera por nada: o pedido segue logo).
// Os tratadores correm nas threads do CEF (IO, UI): nenhuma exceção pode sair daqui (fechava o Éteris inteiro), e os
// endereços dos frames leem-se só pelo SafeFrame (um frame destacado lança ao ler Url).
public sealed class PrivacyRequestHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Action<string> onBlocked,
    Action? onCrashed = null, string? userAgent = null, Action? onMainFrameRequest = null, bool isPrivate = false,
    Func<string, bool>? localNetworkAllowed = null, Action<string, string>? onLocalNetworkBlocked = null,
    Action<IReadOnlyList<string>>? onTrackingSkipped = null, Action<MainNavigation>? onMainNavigation = null,
    Action<string, string>? onCloaked = null, SiteObservatory? observatory = null)
    : CefSharp.Handler.RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
        bool userGesture, bool isRedirect)
    {
        try
        {
            if (onMainNavigation is not null && frame.IsMain)
                onMainNavigation(new MainNavigation(SafeFrame.MainUrl(browser), request.Url, userGesture, isRedirect, request.TransitionType,
                    _resources.TakeRewrite(browser.Identifier, request.Url)));
        }
        catch (Exception) { }
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
        if (request.Headers is not { } headers) return;
        var names = headers.AllKeys.Where(k => k is not null && RequestIdentity.IsIdentifyingHint(k)).ToList();
        if (names.Count == 0) return;
        foreach (var name in names) headers.Remove(name);
        request.Headers = headers;
    }

    // O processo da página morreu (crash, falta de memória): a janela mostra a página de erro em vez de um ecrã vazio.
    protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status,
        int errorCode, string errorMessage)
        => onCrashed?.Invoke();

    private readonly PrivacyResourceHandler _resources = new(blocker, isProtected, onBlocked, userAgent, onMainFrameRequest, isPrivate,
        localNetworkAllowed, onLocalNetworkBlocked, onTrackingSkipped, onCloaked, observatory);

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser,
        IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        => _resources;

    // Com o Éteris a tratar cada pedido, é o CEF que lê e grava os cookies dos pedidos — e o bloqueio de cookies de
    // terceiros do Chromium não chegava lá: um pixel de outro site embutido numa página levava os cookies desse site.
    // Este filtro recusa enviar e gravar cookies de outro site que não o de topo (ThirdPartyCookies.Allow), menos os
    // «Partitioned» (CHIPS) que o terceiro grava no frasco deste site de topo (PartitionedCookieJar), como no Chrome,
    // no Firefox e no Brave. Só existe com o bloqueio ligado (GetCookieAccessFilter).
    internal sealed class ThirdPartyCookieFilter(Func<string, bool> isProtected, bool isPrivate, Func<IBrowser, string?>? topOf = null)
        : ICookieAccessFilter
    {
        private string? Top(IBrowser browser) => topOf is null ? SafeFrame.MainUrl(browser) : topOf(browser);

        public bool CanSendCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, Cookie cookie)
        {
            try
            {
                var top = Top(browser);
                if (Allow(top, request)) return true;
                return TopDocument.SiteKey(top) is { } site
                       && PartitionedCookieJar.For(isPrivate).Holds(site, cookie.Domain, cookie.Name, cookie.Path, cookie.Value);
            }
            // Separador a fechar: sem decisão possível, o cookie de terceiros não passa.
            catch (Exception) { return false; }
        }

        public bool CanSaveCookie(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IResponse response, Cookie cookie)
        {
            try
            {
                var top = Top(browser);
                if (Allow(top, request)) return true;
                if (TopDocument.SiteKey(top) is not { } site
                    || !ThirdPartyCookies.IsPartitioned(response?.Headers?.GetValues("Set-Cookie"), cookie.Name)) return false;
                PartitionedCookieJar.For(isPrivate).Remember(site, cookie.Domain, cookie.Name, cookie.Path, cookie.Value);
                return true;
            }
            catch (Exception) { return false; }
        }

        private bool Allow(string? top, IRequest request)
            => ThirdPartyCookies.Allow(top, request.Url, request.ResourceType, block: true,
                Uri.TryCreate(TopDocument.Effective(top), UriKind.Absolute, out var page) && page.Host.Length > 0 && !isProtected(page.Host));
    }

    // Decide cada pedido do separador: cabeçalhos de privacidade, saltos, limpeza e HTTPS do endereço da página, rede
    // local, modo rigoroso, regras do utilizador, listas de bloqueio e CNAME. Corre na thread IO do CEF.
    internal sealed class PrivacyResourceHandler(Func<IRequestBlocker> blocker, Func<string, bool> isProtected, Action<string> onBlocked,
        string? userAgent, Action? onMainFrameRequest = null, bool isPrivate = false, Func<string, bool>? localNetworkAllowed = null,
        Action<string, string>? onLocalNetworkBlocked = null, Action<IReadOnlyList<string>>? onTrackingSkipped = null,
        Action<string, string>? onCloaked = null, SiteObservatory? observatory = null)
        : CefSharp.Handler.ResourceRequestHandler
    {
        // Observatório do site (Shields/SiteObservatory.cs): cada sub-recurso com a decisão que teve. Um incremento num
        // dicionário por pedido — o resto só se calcula quando o painel abre.
        private void Observe(string url, ResourceType type, ObservedVerdict verdict, string? detail = null)
        {
            observatory?.Record(url, type, verdict, detail);
            // Relatório de privacidade: só os bloqueios, só o domínio do rastreador (a rede local nem isso).
            if (isPrivate || PrivacyCategory.Of(verdict) is not { } category) return;
            PrivacyLedger.Count(category, verdict == ObservedVerdict.LocalNetwork ? null : detail ?? SiteObservatory.HostOf(url).ToString());
        }

        // Desmascarar CNAME (Shields/CnameUncloak.cs) para um pedido que as listas deixaram passar. Com o nome já na
        // cache decide-se logo; senão a consulta corre fora da thread de rede e o pedido espera no máximo
        // CnameUncloak.MaxWait (IRequestCallback) — passado isso segue, e os seguintes já encontram a resposta.
        private CefReturnValue Uncloak(IBrowser browser, IRequest request, string? pageUrl, string? documentUrl, string type,
            IRequestCallback? callback)
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
            var cache = Cname.For(isPrivate);
            if (cache.TryGet(host, out var chain))
            {
                var target = CnameUncloak.CloakedTracker(url, documentUrl, type, chain, engine.ShouldBlock);
                CnameStats.Decision(System.Diagnostics.Stopwatch.GetTimestamp() - started);
                Observe(url, resourceType, target is null ? ObservedVerdict.Allowed : ObservedVerdict.Cloaked, target);
                if (target is null) return CefReturnValue.Continue;
                Cloaked(uri.Host, target);
                return CefReturnValue.Cancel;
            }
            if (callback is null || !Cname.Quota.TryTake(browser.Identifier, pageUrl, host))
            {
                CnameStats.Decision(System.Diagnostics.Stopwatch.GetTimestamp() - started);
                Observe(url, resourceType, ObservedVerdict.Allowed);
                return CefReturnValue.Continue;
            }
            var lookup = cache.LookupAsync(host);
            CnameStats.Decision(System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Task.WhenAny(lookup, Task.Delay(CnameUncloak.MaxWait)).ContinueWith(_ =>
            {
                string? target = null;
                try
                {
                    var answered = lookup.IsCompletedSuccessfully;
                    CnameStats.Waited(System.Diagnostics.Stopwatch.GetTimestamp() - started, timedOut: !answered);
                    target = answered ? CnameUncloak.CloakedTracker(url, documentUrl, type, lookup.Result, engine.ShouldBlock) : null;
                    Observe(url, resourceType, target is null ? ObservedVerdict.Allowed : ObservedVerdict.Cloaked, target);
                    if (target is not null) Cloaked(uri.Host, target);
                }
                catch (Exception) { }
                try
                {
                    using (callback) callback.Continue(target is null);
                }
                catch (Exception) { }
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
            if (!isPrivate)
                foreach (var host in pending.Hosts) PrivacyLedger.Count(PrivacyCategory.Debounce, host);
        }

        // Um redirecionamento do servidor para um salto conhecido (um encurtador que manda para l.facebook.com/l.php?u=…)
        // também vai direto ao destino.
        protected override void OnResourceRedirect(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            IResponse response, ref string newUrl)
        {
            try
            {
                if (request.ResourceType != ResourceType.MainFrame) return;
                if (Jump(newUrl, isProtected) is { } jump)
                {
                    RememberSkip(browser, newUrl);
                    newUrl = jump;
                }
                _navigatingTo[browser.Identifier] = newUrl;
            }
            catch (Exception) { }
        }

        // «HTTPS primeiro»: o https:// subido respondeu (não um redirecionamento): fala HTTPS, a marca da subida sai.
        protected override bool OnResourceResponse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            IResponse response)
        {
            try
            {
                if (request.ResourceType == ResourceType.MainFrame && response.StatusCode is > 0 and not (>= 300 and < 400))
                    ShieldEngine.Https.Settled(request.Url, isPrivate);
            }
            catch (Exception) { }
            return false;
        }

        // Com «Bloquear cookies de terceiros» desligado não há filtro: nada a ler por cookie na thread de rede.
        protected override ICookieAccessFilter? GetCookieAccessFilter(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
            => Data.Settings.Current.BlockThirdPartyCookies ? _cookies : null;

        private ThirdPartyCookieFilter? _cookieFilter;

        private ThirdPartyCookieFilter _cookies => _cookieFilter ??= new(isProtected, isPrivate, TopOf);

        // O documento principal que um pedido vê. Logo depois de uma navegação — um recarregar, sobretudo — o frame
        // principal novo ainda não tem endereço na thread de rede (""), e os primeiros pedidos da página ficavam sem
        // site (sem cookies próprios, sem cartão da rede local). Vale então o endereço para onde a navegação foi.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _navigatingTo = new();

        internal string? TopOf(IBrowser browser)
        {
            var url = SafeFrame.MainUrl(browser);
            if (!string.IsNullOrEmpty(url)) return url;
            try { return _navigatingTo.TryGetValue(browser.Identifier, out var to) ? to : url; }
            catch (Exception) { return url; }
        }

        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
            IRequest request, IRequestCallback callback)
            => Decide(browser, frame, request, callback);

        // Sem callback (testes), a consulta de CNAME nunca fica à espera: o pedido segue.
        internal CefReturnValue Decide(IBrowser browser, IFrame? frame, IRequest request, IRequestCallback? callback = null)
        {
            var blocking = false;
            try
            {
                return DecideUnsafe(browser, frame, request, callback, ref blocking);
            }
            catch (Exception)
            {
                // Frame destacado, separador a fechar…: o pedido segue (ou fica bloqueado, se já se tinha decidido).
                return blocking ? CefReturnValue.Cancel : CefReturnValue.Continue;
            }
        }

        private CefReturnValue DecideUnsafe(IBrowser browser, IFrame? frame, IRequest request, IRequestCallback? callback, ref bool blocking)
        {
            foreach (var (name, value) in PrivacySignals.Headers) request.SetHeaderByName(name, value, true);
            // Uma só língua (a com que o motor arrancou) e só as dicas do cliente de baixa entropia, mesmo que o site peça
            // mais (Shields/RequestIdentity.cs). Sem reescrever o cabeçalho, o Chromium juntava a língua-base com peso.
            // Num fetch/XHR em que a página escolheu a língua (o seletor de línguas do site), a dela fica.
            if (EngineHardening.StartedWithLanguage is { Length: > 0 } language
                && RequestIdentity.AcceptLanguage(request.GetHeaderByName("Accept-Language"), language,
                    request.ResourceType is ResourceType.MainFrame or ResourceType.SubFrame) is { } accept)
                request.SetHeaderByName("Accept-Language", accept, true);
            StripIdentifyingHints(request);
            // Referrer entre sites só com a origem (Shields/ReferrerCap.cs), quando o escudo está ligado no site que o envia.
            if (ReferrerCap.Decide(request.ReferrerUrl, request.Url) is { Kind: not ReferrerCap.Kind.Keep } cap
                && Uri.TryCreate(request.ReferrerUrl, UriKind.Absolute, out var referrer) && isProtected(referrer.Host))
                request.SetReferrer(cap.Value ?? string.Empty, cap.Value is null ? ReferrerPolicy.NoReferrer : ReferrerPolicy.Origin);
            if (userAgent is not null)
                foreach (var (name, value) in MobileHeaders(userAgent)) request.SetHeaderByName(name, value, true);

            if (request.ResourceType == ResourceType.MainFrame)
            {
                var result = DecideMainFrame(browser, request);
                _navigatingTo[browser.Identifier] = request.Url;
                return result;
            }

            // Só pedidos da rede passam pelas listas (eteris://, devtools://, data:, blob: seguem logo).
            if (!IsNetwork(request.Url)) return CefReturnValue.Continue;

            var committed = TopOf(browser);
            if (!string.IsNullOrEmpty(committed)) observatory?.Sync(committed);
            // A página de um blob: é a de quem o criou (Shields/ReferrerCap.cs, TopDocument).
            var pageUrl = TopDocument.Effective(committed);
            // Rede local (Shields/LocalNetwork.cs): uma página pública não chega a este computador nem à rede de casa sem
            // autorização do utilizador para o site — com ou sem escudo, é uma proteção e não um bloqueio de anúncios.
            if (LocalNetworkGate(pageUrl, request, callback) is { } gate)
            {
                blocking = gate == CefReturnValue.Cancel;
                return gate;
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
                blocking = true;
                onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var strictBlocked) ? strictBlocked.Host : request.Url);
                Observe(request.Url, request.ResourceType, ObservedVerdict.Strict);
                return CefReturnValue.Cancel;
            }

            // Domínio que o utilizador bloqueou só neste site, no Observatório (Shields/SiteBlockRules.cs).
            if (SiteRules.Blocks(pageUrl, request.Url, isPrivate))
            {
                blocking = true;
                onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var ruled) ? ruled.Host : request.Url);
                Observe(request.Url, request.ResourceType, ObservedVerdict.UserBlocked);
                return CefReturnValue.Cancel;
            }

            // As opções $domain e $third-party das listas referem-se ao documento que fez o pedido (pode ser um iframe).
            var documentUrl = SafeFrame.Url(frame) is { Length: > 0 } frameUrl && frameUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? frameUrl
                : pageUrl;
            var adblockType = ResourceTypes.ToAdblock(request.ResourceType);
            if (!blocker().ShouldBlock(request.Url, documentUrl, adblockType))
                return Uncloak(browser, request, pageUrl, documentUrl, adblockType, callback);

            blocking = true;
            onBlocked(Uri.TryCreate(request.Url, UriKind.Absolute, out var blocked) ? blocked.Host : request.Url);
            Observe(request.Url, request.ResourceType, ObservedVerdict.Blocked);
            return CefReturnValue.Cancel;
        }

        // A decisão da rede local para um sub-recurso (null: segue para as outras decisões). Uma página de nome público
        // que resolve para a rede local (a intranet, o painel de casa) é ela própria local: aí decide a verificação do
        // Chromium, que vê o IP a que a página se ligou. Saber o endereço não para a thread de rede: com o nome fora da
        // cache, o pedido espera no máximo LocalNetwork.MaxWait (IRequestCallback). Na janela privada e com o DNS cifrado
        // não se pergunta nada ao DNS do sistema: fica o cartão.
        private CefReturnValue? LocalNetworkGate(string? pageUrl, IRequest request, IRequestCallback? callback)
        {
            if (LocalNetwork.Decide(pageUrl, request.Url, request.ResourceType, allowedForSite: false) != LocalNetwork.Decision.Block)
                return null;
            var site = LocalNetwork.SiteOf(pageUrl);
            if (site is not null && localNetworkAllowed?.Invoke(site) == true) return null;
            if (site is not null && !isPrivate && PrivacyPreferences.SecureDnsProvider is null && LocalNetwork.ResolvableTop(pageUrl) is { } top)
            {
                if (LocalNetwork.Addresses.TryGet(top, out var space))
                {
                    if (space != LocalNetwork.Space.Public) return null;
                }
                else if (callback is not null)
                {
                    var url = request.Url;
                    var type = request.ResourceType;
                    var lookup = LocalNetwork.Addresses.LookupAsync(top);
                    Task.WhenAny(lookup, Task.Delay(LocalNetwork.MaxWait)).ContinueWith(_ =>
                    {
                        var local = lookup.IsCompletedSuccessfully && lookup.Result != LocalNetwork.Space.Public;
                        try
                        {
                            if (local) Observe(url, type, ObservedVerdict.Allowed);
                            else BlockLocal(site, url, type);
                        }
                        catch (Exception) { }
                        try
                        {
                            using (callback) callback.Continue(local);
                        }
                        catch (Exception) { }
                    }, TaskScheduler.Default);
                    return CefReturnValue.ContinueAsync;
                }
            }
            BlockLocal(site, request.Url, request.ResourceType);
            return CefReturnValue.Cancel;
        }

        // Sem site (um documento sem origem web) não há a quem perguntar: só fica no Observatório.
        private void BlockLocal(string? site, string url, ResourceType type)
        {
            if (site is not null) onLocalNetworkBlocked?.Invoke(site, url);
            Observe(url, type, ObservedVerdict.LocalNetwork);
        }

        private CefReturnValue DecideMainFrame(IBrowser browser, IRequest request)
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
            // O pedido da página segue logo. (Antes esperava aqui pelo registo dos scriptlets, até 1 s; depois de a
            // navegação começar o DevTools já não entrega esse registo a tempo: ver ScriptletInjector.)
            // O Observatório só recomeça quando a página nova entra em vigor (SiteObservatory.Sync/Commit).
            observatory?.Navigating();
            onMainFrameRequest?.Invoke();
            ReportSkips(browser);
            return CefReturnValue.Continue;
        }

        internal static bool IsNetwork(string url)
            => url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("ws:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("wss:", StringComparison.OrdinalIgnoreCase);
    }
}
