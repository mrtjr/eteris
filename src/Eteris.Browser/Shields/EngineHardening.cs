namespace Eteris.Browser.Shields;

// Interruptores de linha de comandos do motor que cumprem promessas do Éteris. Um nome que não existe é ignorado em
// silêncio pelo Chromium: cada nome destas listas tem de ser uma base::Feature do libcef.dll em uso (o teste
// EngineFeatureNamesTests procura a estrutura no binário) e aparece em eteris://privacy/ com o que faz.
public static class EngineHardening
{
    // As APIs de publicidade do Chromium («Privacy Sandbox»): nenhum site pode pedir ao Éteris um resumo dos
    // interesses do utilizador, leilões de anúncios, medição de conversões nem memória partilhada entre sites.
    public static readonly IReadOnlyList<(string Feature, string What)> AdApis =
    [
        ("BrowsingTopics", "Topics: o navegador resumia os seus interesses para os anunciantes"),
        ("Fledge", "Protected Audience (FLEDGE): leilões de anúncios feitos dentro do navegador"),
        ("ConversionMeasurement", "Attribution Reporting: medir que anúncio levou a uma compra"),
        ("FencedFrames", "Fenced Frames: as molduras onde aparecem os anúncios dos leilões"),
        // Estes dois forçariam as APIs ligadas mesmo sem consentimento: ficam explicitamente desligados.
        ("PrivacySandboxAdsAPIsOverride", "nada força as APIs de anúncios a ficar ligadas"),
        ("PrivacySandboxAdsAPIsM1Override", "nada força as APIs de anúncios a ficar ligadas (M1)"),
    ];

    // As mesmas APIs vistas pelo JavaScript (funcionalidades de execução do Blink): sem elas, document.browsingTopics,
    // navigator.joinAdInterestGroup, window.sharedStorage, attributionsrc e <fencedframe> não existem nas páginas.
    // (Attribution Reporting e Shared Storage só existem aqui: no CEF 156 não são base::Feature do motor.)
    public static readonly IReadOnlyList<string> AdBlinkFeatures =
        ["TopicsAPI", "AdInterestGroupAPI", "Fledge", "AttributionReporting", "SharedStorageAPI", "FencedFrames"];

    // Frascos por site de topo: o que um terceiro guarda ou aprende embutido num site não serve noutro site
    // (ligados explicitamente, mesmo que a versão do motor já os traga ligados). Os cookies «Partitioned» (CHIPS) já
    // não têm interruptor no CEF 156 (estão sempre ligados); o filtro de cookies do Éteris deixa-os no frasco do site
    // de topo (PartitionedCookieJar).
    public static readonly IReadOnlyList<(string Feature, string What)> Partitioning =
    [
        ("ThirdPartyStoragePartitioning", "localStorage, IndexedDB e caches de um terceiro separados por site de topo"),
        ("SplitCacheByNetworkIsolationKey", "cache HTTP separada por site de topo: a cache não denuncia o que visitou noutro site"),
        ("PartitionConnectionsByNetworkIsolationKey", "ligações à rede separadas por site de topo"),
    ];

    // A verificação do próprio Chromium antes de uma página pública chegar à rede local: cobre o que o tratador de
    // pedidos do Éteris não vê (WebSockets, nomes públicos que apontam para endereços locais). O pedido chega como
    // permissão «Rede local» (Permissions/SitePermissionHandler.cs).
    public static readonly IReadOnlyList<(string Feature, string What)> LocalNetworkChecks =
    [
        ("LocalNetworkAccessChecks", "o Chromium pede autorização antes de uma página pública chegar à rede local"),
    ];

    // Uma só língua no cabeçalho Accept-Language: sem isto o Chromium junta a língua-base com peso («pt-PT,pt;q=0.9»).
    public static readonly IReadOnlyList<(string Feature, string What)> Identity =
    [
        ("ReduceAcceptLanguage", "o cabeçalho Accept-Language leva só a língua escolhida"),
        ("ReduceAcceptLanguageHTTP", "também nos pedidos de sub-recursos"),
    ];

    // WebRTC sem o IP da rede e sem portas abertas. A política «disable_non_proxied_udp» vai também na linha de comandos
    // (o Chromium passa --webrtc-ip-handling-policy à preferência webrtc.ip_handling_policy, que fica fixa): está em vigor
    // desde o primeiro processo de página, sem esperar por PrivacyPreferences.ApplyAsync, e vale para a janela privada.
    public const string WebRtcPolicySwitch = "webrtc-ip-handling-policy";
    public const string WebRtcPolicy = "disable_non_proxied_udp";

    // O «respondedor mDNS» do WebRTC só serve para esconder endereços locais que esta política já nunca dá aos sites,
    // mas cada página que criava um RTCPeerConnection o arrancava: UDP 5353 à escuta em todas as interfaces do serviço
    // de rede, e o Windows pedia uma exceção na firewall para o «CefSharp.BrowserSubprocess».
    public static readonly IReadOnlyList<(string Feature, string What)> NoListeningSockets =
    [
        ("WebRtcHideLocalIpsWithMdns", "o WebRTC não deixa a porta mDNS (UDP 5353) à escuta em todas as interfaces — o Windows não pede exceção na firewall"),
    ];

    // O que o Chromium ainda mandava à Google sozinho em cada arranque, apesar do disable-background-networking e do
    // disable-component-update (medido com um proxy local que regista todos os pedidos, docs/paridade.md, secção 7):
    // - o relógio de rede (components/network_time/network_time_tracker.cc) pedia a hora a
    //   http://clients2.google.com/time/1/current logo ao arrancar e depois de tempos a tempos. Só serve para dizer
    //   «o relógio do computador está errado» numa página de erro de certificado; sem ele o Chromium continua a dar esse
    //   aviso pela data da própria compilação.
    // - o «Modo IA» da pesquisa Google (components/omnibox/browser/aim_eligibility_service.cc, «kill switch» kAimEnabled)
    //   perguntava a https://www.google.com/async/folae?udm=50&client_locale=…&client_country=… se o utilizador o pode
    //   usar — com os cookies da Google, porque o motor de pesquisa por omissão do Chromium é a Google. A barra de
    //   endereço do Éteris é dele e não usa o «Modo IA»: pesquisar e abrir sites da Google continua igual.
    public static readonly IReadOnlyList<(string Feature, string What)> NoGoogleAtStartup =
    [
        ("NetworkTimeServiceQuerying", "o relógio de rede do Chromium não pergunta a hora a clients2.google.com"),
        ("AimEnabled", "o «Modo IA» da pesquisa Google não pergunta a www.google.com, com os cookies da Google, se o pode usar"),
    ];

    // Tudo o que o Éteris acrescenta à configuração do motor: interruptores e uma só língua para os sites (a lista de
    // línguas fixa-se ao arrancar; o cabeçalho Accept-Language e o navigator.languages seguem-na).
    // A língua do motor (Locale) é a mesma: é ela que dá o locale por omissão do Intl nas páginas e nos workers
    // (Intl.DateTimeFormat().resolvedOptions().locale, toLocaleString()). Com o Locale fixo em pt-PT e os sites em
    // pt-BR, o Intl dizia pt-PT e o navigator.language pt-BR — uma incoerência que o Chrome não tem (medido,
    // docs/paridade.md #6). Só muda os textos do próprio Chromium (páginas de erro, validação de formulários); a
    // interface do Éteris é dele e continua em português europeu.
    public static void Configure(CefSharp.CefSettingsBase settings, string? language = null)
    {
        Apply(settings.CefCommandLineArgs);
        settings.AcceptLanguageList = language ?? PrivacyPreferences.SiteLanguage;
        settings.Locale = settings.AcceptLanguageList;
        StartedWithLanguage = settings.AcceptLanguageList;
    }

    // A língua com que o motor arrancou (eteris://privacy/ verifica que é uma só).
    public static string StartedWithLanguage { get; private set; } = string.Empty;

    // Junta as listas do Éteris às que já estão na linha de comandos (sem repetir nomes).
    public static void Apply(IDictionary<string, string> args)
    {
        Merge(args, "enable-features", Partitioning.Concat(Identity).Concat(LocalNetworkChecks).Select(f => f.Feature));
        Merge(args, "disable-features", AdApis.Concat(NoListeningSockets).Concat(NoGoogleAtStartup).Select(a => a.Feature));
        args[WebRtcPolicySwitch] = WebRtcPolicy;
        Merge(args, "disable-blink-features", AdBlinkFeatures);
        Applied = new Dictionary<string, string>(args);
    }

    // A linha de comandos dada ao motor na última configuração (o estado real que eteris://privacy/ verifica).
    public static IReadOnlyDictionary<string, string> Applied { get; private set; } = new Dictionary<string, string>();

    public static IReadOnlyList<string> Switch(IReadOnlyDictionary<string, string> args, string name)
        => args.TryGetValue(name, out var value) ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    private static void Merge(IDictionary<string, string> args, string name, IEnumerable<string> features)
    {
        var current = args.TryGetValue(name, out var value) ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        args[name] = string.Join(',', current.Concat(features).Distinct(StringComparer.Ordinal));
    }
}
