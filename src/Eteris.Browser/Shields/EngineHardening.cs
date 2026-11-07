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

    // Junta as listas do Éteris às que já estão na linha de comandos (sem repetir nomes).
    public static void Apply(IDictionary<string, string> args)
    {
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
