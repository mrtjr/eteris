using System.IO;
using System.Text.Json;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Browser;

// eteris://privacy/ — o que o Éteris promete, cada promessa com ✔/✘ lido do estado em vigor (a linha de comandos que
// o motor recebeu, as preferências que o Chromium aceitou, os cabeçalhos que o tratador de pedidos põe, a afinidade das
// janelas no Windows) e, quando dá, confirmado ao vivo pelo JavaScript da própria página.
public static class PrivacyPage
{
    public const string Url = "eteris://privacy/";

    public const string ReferrerSample = "https://www.exemplo.pt/artigo?id=42";

    // Dicas que um site pode pedir (Accept-CH) e que separam este computador dos outros.
    public static readonly string[] IdentifyingHints =
    [
        "Sec-CH-UA-Full-Version-List", "Sec-CH-UA-Full-Version", "Sec-CH-UA-Platform-Version", "Sec-CH-UA-Arch",
        "Sec-CH-UA-Bitness", "Sec-CH-UA-Model", "Sec-CH-UA-WoW64", "Sec-CH-UA-Form-Factors", "Sec-CH-Device-Memory",
        "Sec-CH-Viewport-Width", "Sec-CH-DPR", "Device-Memory",
    ];

    // Kept: true = cumprida, false = falhou, null = opcional e desligada (escolha do utilizador).
    public sealed record Promise(string Id, string Title, string Detail, bool? Kept, string? Live = null, string? Fix = null);

    public sealed record State(
        IReadOnlyDictionary<string, string> EngineArgs,
        IReadOnlyList<(string Name, string Value)> Headers,
        bool GpcScript,
        bool ThirdPartyCookiesBlocked,
        bool WebRtcProtected,
        string? SecureDns,
        string Language,
        bool HideFromCapture,
        int VisibleWindows,
        int HiddenWindows,
        bool SkipRedirects = true,
        bool ForgetBounces = true,
        int BouncesCleared = 0,
        int BouncesPending = 0,
        bool UncloakCname = true,
        long CnameBlocked = 0,
        long CnameDecisions = 0,
        double CnameMicros = 0,
        CnameSource CnameSource = CnameSource.WindowsDns);

    // Exemplos à vista, decididos pelas mesmas funções que o tratador de pedidos usa.
    public const string BounceSample = "https://l.facebook.com/l.php?u=https%3A%2F%2Fwww.exemplo.pt%2Fartigo";
    public const string CnameSample = "https://metrics.exemplo.pt/col.js";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(PrivacyPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.privacy.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static IReadOnlyList<Promise> Promises(State s)
    {
        var disabled = EngineHardening.Switch(s.EngineArgs, "disable-features");
        var hiddenFromJs = EngineHardening.Switch(s.EngineArgs, "disable-blink-features");
        var missingAds = EngineHardening.AdApis.Where(a => !disabled.Contains(a.Feature)).Select(a => a.Feature)
            .Concat(EngineHardening.AdBlinkFeatures.Where(f => !hiddenFromJs.Contains(f))).Distinct().ToList();
        var quiet = s.EngineArgs.ContainsKey("disable-background-networking") && s.EngineArgs.ContainsKey("disable-component-update")
                    && s.EngineArgs.TryGetValue("component-updater", out var updater) && updater.Contains(Infrastructure.CefConfigurator.NoUpdateServer, StringComparison.Ordinal);
        var gpcHeader = s.Headers.Contains(("Sec-GPC", "1")) && s.Headers.Contains(("DNT", "1"));
        var enabled = EngineHardening.Switch(s.EngineArgs, "enable-features");
        var missingJars = EngineHardening.Partitioning.Where(p => !enabled.Contains(p.Feature)).Select(p => p.Feature).ToList();
        // O referrer é decidido pela mesma função que o tratador de pedidos usa, com um exemplo à vista.
        var capped = ReferrerCap.Decide(ReferrerSample, "https://outro-site.example/");
        var sameSite = ReferrerCap.Decide(ReferrerSample, "https://www.exemplo.pt/outra");
        var oneLanguage = s.Language.Length > 0 && !s.Language.Contains(',') && EngineHardening.Identity.All(i => enabled.Contains(i.Feature));
        // As dicas são decididas pela mesma função que o tratador de pedidos usa.
        var hintsCut = IdentifyingHints.All(RequestIdentity.IsIdentifyingHint) && !RequestIdentity.LowEntropyHints.Any(RequestIdentity.IsIdentifyingHint);
        // A decisão da rede local é a mesma função que o tratador de pedidos usa.
        var lanKept = LocalNetwork.Decide("https://www.exemplo.pt/", "http://192.168.1.1/", CefSharp.ResourceType.Xhr, false) == LocalNetwork.Decision.Block
                      && LocalNetwork.Decide("https://www.exemplo.pt/", "http://127.0.0.1:6463/", CefSharp.ResourceType.Xhr, false) == LocalNetwork.Decision.Block
                      && LocalNetwork.Decide("https://www.exemplo.pt/", "http://192.168.1.1/", CefSharp.ResourceType.MainFrame, false) == LocalNetwork.Decision.Allow
                      && EngineHardening.LocalNetworkChecks.All(c => enabled.Contains(c.Feature));
        var debounced = Debounce.Destination(BounceSample) == "https://www.exemplo.pt/artigo";
        var uncloaks = CnameUncloak.IsCandidate("https://www.exemplo.pt/", CnameSample, CefSharp.ResourceType.Script)
                       && CnameUncloak.CloakedTracker(CnameSample, "https://www.exemplo.pt/", "script", ["exemplo.eulerian.net"],
                           (url, _, _) => url.StartsWith("https://exemplo.eulerian.net/", StringComparison.Ordinal)) == "exemplo.eulerian.net";
        // WebRTC: a política em vigor no perfil, fixada na linha de comandos do motor, e sem o mDNS que abria portas.
        var webRtcFixed = EngineHardening.Switch(s.EngineArgs, EngineHardening.WebRtcPolicySwitch).SequenceEqual([EngineHardening.WebRtcPolicy]);
        var webRtcQuiet = EngineHardening.NoListeningSockets.All(f => disabled.Contains(f.Feature));
        var referrerKept = capped is { Kind: ReferrerCap.Kind.Origin, Value: "https://www.exemplo.pt/" } && sameSite.Kind == ReferrerCap.Kind.Keep;

        return
        [
            new("gpc-header", "Pede a cada site para não o seguir",
                gpcHeader ? "Cada pedido leva os cabeçalhos «Sec-GPC: 1» (Global Privacy Control) e «DNT: 1»." : "Os pedidos saem sem o cabeçalho Sec-GPC.",
                gpcHeader),
            new("gpc-js", "O pedido é visível ao JavaScript dos sites",
                s.GpcScript ? "navigator.globalPrivacyControl vale true em todas as páginas — é aí que os avisos de cookies e o Consent Mode o leem."
                    : "O script do GPC não está registado.",
                s.GpcScript, Live: "gpc"),
            new("ad-apis", "Sem as APIs de anúncios do Chromium",
                missingAds.Count == 0
                    ? "Topics, Protected Audience (FLEDGE), Attribution Reporting, Shared Storage e Fenced Frames estão desligados no motor."
                    : "Ainda ligadas no motor: " + string.Join(", ", missingAds) + ".",
                missingAds.Count == 0, Live: "ads"),
            new("quiet", "Nada sai do computador sem um pedido seu",
                quiet ? "Sem atualizações automáticas de componentes da Google nem tráfego de fundo do Chromium."
                    : "O motor arrancou sem os interruptores que cortam o tráfego de fundo.",
                quiet),
            new("cookies", "Cookies de terceiros bloqueados",
                s.ThirdPartyCookiesBlocked
                    ? "Os anunciantes embutidos nas páginas não guardam, não leem e não recebem cookies — nem no JavaScript, nem nos pedidos à rede."
                    : "Desligado nas Definições: os terceiros podem guardar cookies.",
                s.ThirdPartyCookiesBlocked, Fix: s.ThirdPartyCookiesBlocked ? null : "eteris://settings/#privacidade"),
            new("jars", "Cada site com o seu frasco",
                missingJars.Count == 0
                    ? "O que um terceiro guarda embutido num site (armazenamento, cookies «Partitioned», cache, ligações) não serve noutro site."
                    : "Por separar no motor: " + string.Join(", ", missingJars) + ".",
                missingJars.Count == 0),
            new("referrer", "Para outro site, o referrer leva só a origem",
                referrerKept
                    ? $"Um link de {ReferrerSample} para outro site leva só «{capped.Value}», mesmo que a página peça mais; dentro do mesmo site nada muda."
                    : "O referrer entre sites não está a ser reduzido à origem.",
                referrerKept),
            new("language", "Uma só língua para os sites",
                oneLanguage
                    ? $"Os sites recebem «{s.Language}» e mais nada — sem a lista de línguas com pesos que ajuda a distingui-lo."
                    : "A língua enviada aos sites não ficou reduzida a uma só.",
                oneLanguage, Live: "language", Fix: "eteris://settings/#privacidade"),
            new("hints", "Dicas do cliente mínimas",
                hintsCut
                    ? "Os sites recebem só a marca do navegador, se é telemóvel e a plataforma; versão completa, arquitetura, modelo, versão do Windows, memória e ecrã nunca saem, mesmo que os peçam."
                    : "Há dicas do cliente que identificam o computador a sair nos pedidos.",
                hintsCut),
            new("lan", "Os sites não falam com a sua rede local",
                lanKept
                    ? "Uma página da Internet não chega a este computador (127.0.0.1, localhost) nem ao router, à impressora ou ao NAS sem o deixar; escrever um endereço local na barra continua livre."
                    : "Os pedidos de páginas públicas para a rede local não estão a ser travados.",
                lanKept),
            new("bounce", "Links sem saltos de rastreio",
                !s.SkipRedirects ? "Opcional · desligado: os links passam pelos intermediários (l.facebook.com, google.com/url…) como o site os escreveu."
                    : !debounced ? "Os saltos de rastreio não estão a ser evitados."
                    : "Um link por l.facebook.com/l.php?u=… vai direto a «www.exemplo.pt/artigo»: o intermediário nunca sabe do clique. "
                      + (s.ForgetBounces
                          ? $"Os sites que só servem de salto perdem cookies e armazenamento 1 hora depois ({Count(s.BouncesCleared, "site esquecido", "sites esquecidos")}, {Count(s.BouncesPending, "à espera", "à espera")})."
                          : "Esquecer os sites de salto está desligado."),
                !s.SkipRedirects ? null : debounced, Fix: s.SkipRedirects ? null : "eteris://settings/#privacidade"),
            new("cname", "Rastreadores escondidos desmascarados",
                !s.UncloakCname ? "Opcional · desligado: um rastreador servido por um subdomínio do site (CNAME) passa como se fosse do site."
                    : !uncloaks ? "O desmascarar de CNAME não está a decidir como devia."
                    : s.CnameSource == CnameSource.Off
                        ? "Em pausa: há um proxy configurado no Windows e perguntar o nome canónico ao DNS do sistema revelaria à rede os nomes que o proxy esconde. Com o DNS cifrado ligado, o próprio motor pergunta-o cifrado."
                    : $"Um pedido a {new Uri(CnameSample).Host} que no DNS é exemplo.eulerian.net é bloqueado como o rastreador que é. "
                      + $"Nesta sessão: {Count((int)s.CnameBlocked, "bloqueado", "bloqueados")} em {Count((int)s.CnameDecisions, "pedido visto", "pedidos vistos")}"
                      + (s.CnameDecisions > 0 ? FormattableString.Invariant($", {s.CnameMicros:0.0} µs por pedido na thread de rede") : string.Empty)
                      + (s.CnameSource == CnameSource.SecureDns && s.SecureDns is { } doh
                          ? $"; o nome canónico pergunta-se a {doh} pelo próprio motor, cifrado e sem cache."
                          : "."),
                !s.UncloakCname ? null : uncloaks && s.CnameSource != CnameSource.Off,
                Fix: !s.UncloakCname || s.CnameSource == CnameSource.Off ? "eteris://settings/#privacidade" : null),
            new("webrtc", "O WebRTC não revela o seu IP local",
                !s.WebRtcProtected ? "A política de IP do WebRTC não foi aplicada."
                    : !webRtcFixed ? "A política de IP do WebRTC não está fixada no motor desde o arranque: as primeiras páginas podiam ficar sem ela."
                    : !webRtcQuiet ? "Os sites não descobrem os endereços da sua rede, mas uma página com WebRTC ainda abre portas à escuta (mDNS) e o Windows pode pedir exceção na firewall."
                    : "As chamadas de vídeo funcionam, mas os sites não descobrem os endereços da sua rede — e o WebRTC já não deixa a porta mDNS à escuta neste computador.",
                s.WebRtcProtected && webRtcFixed && webRtcQuiet),
            new("dns", "DNS cifrado",
                s.SecureDns is { } dns ? $"Os nomes dos sites vão cifrados para {dns}." : "Opcional · desligado: os nomes dos sites vão para o DNS do sistema.",
                s.SecureDns is null ? null : true, Fix: s.SecureDns is null ? "eteris://settings/#privacidade" : null),
            new("recall", "Escondido do Recall e das capturas de ecrã",
                !s.HideFromCapture ? "Opcional · desligado: com ele ligado as capturas, gravações e partilhas de ecrã mostram um vazio."
                    : s.HiddenWindows >= s.VisibleWindows && s.VisibleWindows > 0 ? $"As {s.VisibleWindows} janela(s) do Éteris ficam fora do Recall e das capturas."
                    : $"Só {s.HiddenWindows} de {s.VisibleWindows} janela(s) aceitaram ficar escondidas.",
                !s.HideFromCapture ? null : s.HiddenWindows >= s.VisibleWindows && s.VisibleWindows > 0,
                Fix: "eteris://settings/#privacidade"),
        ];
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public static State Current()
    {
        var (visible, hidden) = Infrastructure.CaptureShield.Count();
        var settings = Data.Settings.Current;
        var bounces = MainWindow.Bounces;
        return new(EngineHardening.Applied, PrivacySignals.Headers, PrivacySignals.ScriptFor("https://example.com/") is not null,
            PrivacyPreferences.ThirdPartyCookiesBlocked, PrivacyPreferences.WebRtcIpProtected, PrivacyPreferences.SecureDnsProvider,
            EngineHardening.StartedWithLanguage, settings.HideFromCapture, visible, hidden,
            settings.SkipTrackingRedirects, settings.ForgetBounceTrackers, bounces.Cleared, bounces.PendingCount,
            settings.UncloakCname, CnameStats.BlockedCount, CnameStats.DecisionCount, CnameStats.MicrosPerDecision, Cname.SourceFor(isPrivate: false));
    }

    // Depois de «Apagar o relatório»: volta à página (sem script, a CSP da página não deixa) e sem repetir o pedido.
    public const string ReportClearedRedirect =
        "<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url=eteris://privacy/#relatorio\"><title>Relatório apagado</title>";

    public static string CategoryLabel(string category) => category switch
    {
        PrivacyCategory.Lists => "Rastreadores e anúncios (listas)",
        PrivacyCategory.Strict => "Modo rigoroso",
        PrivacyCategory.Cloaked => "Escondidos por CNAME",
        PrivacyCategory.User => "Bloqueados por si",
        PrivacyCategory.LocalNetwork => "Pedidos à rede local",
        PrivacyCategory.Debounce => "Saltos de rastreio removidos",
        PrivacyCategory.Forgotten => "Sites de salto esquecidos",
        _ => category,
    };

    private static readonly string[] CategoryOrder =
    [
        PrivacyCategory.Lists, PrivacyCategory.Cloaked, PrivacyCategory.Strict, PrivacyCategory.User, PrivacyCategory.LocalNetwork,
        PrivacyCategory.Debounce, PrivacyCategory.Forgotten,
    ];

    // Os dados do relatório para a página (7 e 30 dias); null na janela privada.
    public static object? ReportJson(IReadOnlyList<PrivacyEvent>? events, DateOnly today)
    {
        if (events is null) return null;
        object Range(int days)
        {
            var r = PrivacyReport.Build(events, today, days);
            return new
            {
                days, blocked = r.Blocked, summary = r.Summary, perDay = r.PerDay,
                categories = CategoryOrder.Select(c => new { key = c, label = CategoryLabel(c), count = r.ByCategory.GetValueOrDefault(c) }),
                companies = r.TopCompanies.Select(c => new { name = c.Name, count = c.Count }),
                trackers = r.TopTrackers.Select(t => new { domain = t.Domain, company = t.Company, count = t.Count }),
                cloaked = r.CloakedExamples,
            };
        }
        return new { today = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), token = InternalPages.Token, week = Range(7), month = Range(30) };
    }

    public static string Render(State state, IReadOnlyList<PrivacyEvent>? events = null, DateOnly? today = null)
    {
        var promises = Promises(state);
        var data = new
        {
            promises = promises.Select(p => new { id = p.Id, title = p.Title, detail = p.Detail, kept = p.Kept, live = p.Live, fix = p.Fix }),
            report = ReportJson(events, today ?? DateOnly.FromDateTime(DateTime.Today)),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
