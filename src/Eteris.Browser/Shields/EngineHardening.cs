namespace Eteris.Browser.Shields;

// Interruptores de linha de comandos do motor que cumprem promessas do Éteris. Os nomes foram confirmados no
// libcef.dll do CEF 156 (um nome desconhecido seria ignorado em silêncio, por isso cada um tem teste e aparece em
// eteris://privacy/ com o que faz).
public static class EngineHardening
{
    // As APIs de publicidade do Chromium («Privacy Sandbox»): nenhum site pode pedir ao Éteris um resumo dos
    // interesses do utilizador, leilões de anúncios, medição de conversões nem memória partilhada entre sites.
    public static readonly IReadOnlyList<(string Feature, string What)> AdApis =
    [
        ("BrowsingTopics", "Topics: o navegador resumia os seus interesses para os anunciantes"),
        ("Fledge", "Protected Audience (FLEDGE): leilões de anúncios feitos dentro do navegador"),
        ("ConversionMeasurement", "Attribution Reporting: medir que anúncio levou a uma compra"),
        ("AttributionReporting", "Attribution Reporting (cabeçalhos e atributos attributionsrc)"),
        ("SharedStorageAPI", "Shared Storage: memória partilhada entre sites para anúncios"),
        ("FencedFrames", "Fenced Frames: as molduras onde aparecem os anúncios dos leilões"),
        // Estes dois forçariam as APIs ligadas mesmo sem consentimento: ficam explicitamente desligados.
        ("PrivacySandboxAdsAPIsOverride", "nada força as APIs de anúncios a ficar ligadas"),
        ("PrivacySandboxAdsAPIsM1Override", "nada força as APIs de anúncios a ficar ligadas (M1)"),
    ];

    // As mesmas APIs vistas pelo JavaScript (funcionalidades de execução do Blink): sem elas, document.browsingTopics,
    // navigator.joinAdInterestGroup, window.sharedStorage e <fencedframe> não existem nas páginas.
    public static readonly IReadOnlyList<string> AdBlinkFeatures =
        ["TopicsAPI", "AdInterestGroupAPI", "Fledge", "AttributionReporting", "SharedStorageAPI", "FencedFrames"];

    // Frascos por site de topo: o que um terceiro guarda ou aprende embutido num site não serve noutro site
    // (ligados explicitamente, mesmo que a versão do motor já os traga ligados).
    public static readonly IReadOnlyList<(string Feature, string What)> Partitioning =
    [
        ("ThirdPartyStoragePartitioning", "localStorage, IndexedDB e caches de um terceiro separados por site de topo"),
        ("PartitionedCookies", "cookies «Partitioned» (CHIPS) guardados num frasco por site de topo"),
        ("SplitCacheByNetworkIsolationKey", "cache HTTP separada por site de topo: a cache não denuncia o que visitou noutro site"),
        ("PartitionConnectionsByNetworkIsolationKey", "ligações à rede separadas por site de topo"),
    ];

    // Uma só língua no cabeçalho Accept-Language: sem isto o Chromium junta a língua-base com peso («pt-PT,pt;q=0.9»).
    public static readonly IReadOnlyList<(string Feature, string What)> Identity =
    [
        ("ReduceAcceptLanguage", "o cabeçalho Accept-Language leva só a língua escolhida"),
        ("ReduceAcceptLanguageHTTP", "também nos pedidos de sub-recursos"),
    ];

    // Tudo o que o Éteris acrescenta à configuração do motor: interruptores e uma só língua para os sites (a lista de
    // línguas fixa-se ao arrancar; o cabeçalho Accept-Language e o navigator.languages seguem-na).
    public static void Configure(CefSharp.CefSettingsBase settings, string? language = null)
    {
        Apply(settings.CefCommandLineArgs);
        settings.AcceptLanguageList = language ?? PrivacyPreferences.SiteLanguage;
        StartedWithLanguage = settings.AcceptLanguageList;
    }

    // A língua com que o motor arrancou (eteris://privacy/ verifica que é uma só).
    public static string StartedWithLanguage { get; private set; } = string.Empty;

    // Junta as listas do Éteris às que já estão na linha de comandos (sem repetir nomes).
    public static void Apply(IDictionary<string, string> args)
    {
        Merge(args, "enable-features", Partitioning.Select(p => p.Feature).Concat(Identity.Select(i => i.Feature)));
        Merge(args, "disable-features", AdApis.Select(a => a.Feature));
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
