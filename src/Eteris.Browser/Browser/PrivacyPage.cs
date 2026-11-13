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
    public sealed record Promise(string Id, string Title, string Detail, bool? Kept, string? Live = null, string? Fix = null, string? FixLabel = null);

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
        CnameSource CnameSource = CnameSource.WindowsDns,
        bool SyncOn = false,
        bool LeakCheckOff = true,
        bool ChromiumPasswordsOff = true,
        Infrastructure.Isolation Sandbox = Infrastructure.Isolation.On,
        bool NetworkSandboxed = true,
        bool SandboxRetryPending = false,
        bool FullShield = true,
        bool HttpsFirst = true);

    // «Voltar a ligar» (D6): pede ao arranque seguinte para voltar a tentar o sandbox (precisa do token da página).
    public const string SandboxRetryUrl = "eteris://privacy/sandbox-retry";

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
        // Os formulários enviados aos sites: sem a verificação de fugas do Chromium (preferência lida do perfil) e sem o
        // servidor de previsões de formulários da Google (interruptor do motor).
        var formsToGoogle = EngineHardening.NoGoogleOnPages.Where(f => !disabled.Contains(f.Feature)).Select(f => f.Feature).ToList();
        var formsQuiet = s.LeakCheckOff && formsToGoogle.Count == 0;
        // Só o GPC: o «DNT: 1» saiu porque distinguia quem o envia (docs/paridade.md #6).
        var gpcHeader = s.Headers.Contains(("Sec-GPC", "1")) && !s.Headers.Any(h => h.Name == "DNT");
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
        var googleStillOn = EngineHardening.NoGoogleAtStartup.Where(f => !disabled.Contains(f.Feature)).Select(f => f.Feature).ToList();
        var referrerKept = capped is { Kind: ReferrerCap.Kind.Origin, Value: "https://www.exemplo.pt/" } && sameSite.Kind == ReferrerCap.Kind.Keep;
        // Cache dos shaders da GPU no disco (ajuste e3 do plano de desempenho): ligada quando o motor arrancou sem o interruptor.
        var gpuCache = !s.EngineArgs.ContainsKey("disable-gpu-shader-disk-cache");
        // Afinações de desempenho do motor (fase 2 do plano de desempenho): as que estão na linha de comandos em vigor.
        var tuning = EnginePerformance.ActiveIn(s.EngineArgs).Where(f => !f.Disable).ToList();
        var fingerprint = tuning.Where(f => f.ChangesFingerprint).ToList();

        return
        [
            SandboxPromise(s),
            new("gpc-header", "Pede a cada site para não o seguir",
                gpcHeader ? "Cada pedido leva o cabeçalho «Sec-GPC: 1» (Global Privacy Control). O antigo «DNT: 1» já não vai: o Chrome e o Brave não o enviam e só servia para o distinguir." : "Os pedidos saem sem o cabeçalho Sec-GPC.",
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
                !quiet ? "O motor arrancou sem os interruptores que cortam o tráfego de fundo."
                    : !s.LeakCheckOff ? "A verificação de fugas do Chromium está ligada: cada palavra-passe enviada a um site ia também, com hash, para passwordsleakcheck-pa.googleapis.com."
                    : formsToGoogle.Count > 0 ? "Ainda ligado no motor: " + string.Join(", ", formsToGoogle) + " (os formulários das páginas iam para o servidor de previsões da Google)."
                    : "Sem atualizações automáticas de componentes da Google nem tráfego de fundo do Chromium. Nem os formulários: a palavra-passe que envia a um site não vai para a verificação de fugas da Google (passwordsleakcheck-pa.googleapis.com) e os formulários das páginas não vão para o servidor de previsões dela (content-autofill.googleapis.com).",
                quiet && formsQuiet),
            new("passwords", "As suas palavras-passe não vão para ninguém",
                !s.ChromiumPasswordsOff ? "O gestor de palavras-passe e o preenchimento automático do Chromium não ficaram desligados no perfil (credentials_enable_service, autofill.profile_enabled, autofill.credit_card_enabled)."
                    : !s.LeakCheckOff ? "A verificação de fugas do Chromium está ligada: cada palavra-passe enviada a um site ia também, com hash, para a Google."
                    : formsToGoogle.Count > 0 ? "Ainda ligado no motor: " + string.Join(", ", formsToGoogle) + " (os formulários das páginas iam para o servidor de previsões da Google)."
                    : "O gestor de palavras-passe e o preenchimento automático do Chromium estão desligados (lidos no perfil: credentials_enable_service, autofill.profile_enabled e autofill.credit_card_enabled a falso), sem a verificação de fugas da Google e sem o servidor de formulários dela (AutofillServerCommunication desligado no motor). "
                      + "As palavras-passe, os cartões e os documentos ficam no cofre do Éteris, cifrado neste computador (AES-256-GCM, com a chave protegida pelo Windows), e o cofre não faz nenhum pedido de rede. "
                      + (s.SyncOn
                          ? "Com a sincronização ligada, só as linhas escolhidas em «O que passa entre PCs» passam para os seus outros computadores, pela pasta da aplicação no seu Google Drive, cifradas neste computador e com uma segunda cifra própria: a Google não as consegue ler."
                          : "Com a sincronização desligada, não saem deste computador."),
                s.ChromiumPasswordsOff && s.LeakCheckOff && formsToGoogle.Count == 0),
            new("no-google", "No arranque, nada sai para a Google",
                googleStillOn.Count == 0
                    ? "Nem o relógio de rede do Chromium (clients2.google.com) nem a verificação do «Modo IA» da pesquisa Google (www.google.com, com os cookies da Google): a Google só recebe um pedido quando pesquisa nela ou abre um site dela."
                      + (s.SyncOn ? " " + SyncException + " " + SyncSees : "")
                    : "Ainda ligados no motor: " + string.Join(", ", googleStillOn) + ".",
                googleStillOn.Count == 0),
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
                    ? "Os sites recebem só a marca do navegador, se é telemóvel e a plataforma; versão completa, arquitetura, modelo, versão do Windows, memória e ecrã nunca saem nos pedidos, mesmo que os peçam, e o JavaScript das páginas recebe valores genéricos (a versão sem os números menores, «Windows 10», sem modelo)."
                    : "Há dicas do cliente que identificam o computador a sair nos pedidos.",
                hintsCut, Live: "hints"),
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
            new("gpu-cache", "A cache da GPU vai-se com os seus dados",
                gpuCache
                    ? "Os shaders da GPU ficam no disco para as páginas abrirem mais depressa, como no Chrome. Não são separados por site: apagam-se ao sair com «Apagar ao fechar», depois de apagar a cache em «Apagar dados» e depois de usar uma janela privada."
                    : "Os shaders da GPU não ficam no disco: o motor volta a prepará-los em cada sessão.",
                true),
            new("engine-tuning", "As afinações de desempenho não mudam o que os sites medem",
                tuning.Count == 0
                    ? "Opcional · nenhuma ligada: o motor corre com os valores do Chromium. O Chrome recebe afinações destas por variações da Google; o Éteris não fala com esse serviço."
                    : fingerprint.Count == 0
                        ? "Ligadas: " + string.Join("; ", tuning.Select(f => f.What)) + ". Nenhuma muda o resultado do canvas nem outro valor que identifique este computador."
                        : "Ligadas: " + string.Join("; ", tuning.Select(f => f.What)) + ". Atenção: "
                          + string.Join(", ", fingerprint.Select(f => EnginePerformance.FeatureName(f.Feature)))
                          + " muda o desenho do canvas, e a impressão digital passa a ser a de um Chrome com o mesmo motor de desenho. Desliga-se com --eteris-sem-ajustes-motor.",
                tuning.Count == 0 ? null : fingerprint.Count == 0),
            new("shield", "Escudo com as listas completas",
                s.FullShield
                    ? "O motor do Escudo carregou as listas completas (filtros de rede, cosméticos e scriptlets)."
                    : "O motor do Escudo não carregou (eteris_shields.dll em falta ou bloqueada): só a lista mínima embutida está a bloquear.",
                s.FullShield),
            new("https-first", "HTTPS primeiro",
                s.HttpsFirst
                    ? "Os endereços http:// abrem por HTTPS; quando o site não o tem, o Éteris avisa antes de abrir."
                    : "Opcional · desligado: os endereços http:// abrem como foram escritos.",
                s.HttpsFirst ? true : null, Fix: s.HttpsFirst ? null : "eteris://settings/#privacidade"),
        ];
    }

    // A linha do isolamento das páginas (INF-10): ✔ só com o sandbox em vigor; desligado, diz porquê e, depois do recuo
    // por precaução, oferece «Voltar a ligar».
    private static Promise SandboxPromise(State s)
    {
        const string title = "Páginas isoladas (sandbox)";
        switch (s.Sandbox)
        {
            case Infrastructure.Isolation.On:
                return new("sandbox", title,
                    "Cada página corre fechada, sem acesso aos seus ficheiros nem ao sistema."
                    + (s.NetworkSandboxed ? string.Empty : " O serviço de rede corre fora do AppContainer: a pasta desta cópia não tem a permissão que a instalação dá."),
                    true);
            case Infrastructure.Isolation.OffBySwitch:
                return new("sandbox", title,
                    "Desligado pelo interruptor --eteris-no-sandbox na linha de comandos: tire-o do atalho.", false);
            case Infrastructure.Isolation.OffAfterFailures:
                var detail = "Desligado por precaução: o Éteris não arrancou bem com ele nas duas últimas vezes.";
                return s.SandboxRetryPending
                    ? new("sandbox", title, detail + " Volta a ser ligado no próximo arranque.", false)
                    : new("sandbox", title, detail, false, Fix: SandboxRetryUrl, FixLabel: "Voltar a ligar →");
            case Infrastructure.Isolation.OffRetried:
                return new("sandbox", title,
                    "Desligado neste arranque: o Éteris não conseguiu arrancar com ele. Da próxima vez volta a tentar.", false);
            default:
                return new("sandbox", title, "Esta versão do Éteris foi publicada sem o isolamento das páginas.", false);
        }
    }

    // A exceção que o utilizador escolheu: com sessão Google para a sincronização, os pedidos dela (cifrados, para a pasta
    // oculta da aplicação no Drive dele, por um cliente próprio que só fala com os endereços da Google necessários — nada
    // pelo motor), a renovação da sessão, o nome e o e-mail da conta e as ações que o próprio utilizador pede.
    // Os momentos são a lista do teste NetworkMoments_ClosedListOfCallers (SyncIntegrationTests): quem acrescentar um
    // momento de rede acrescenta-o a esse teste e a esta frase. Os números são os das constantes (SyncSchedule,
    // PairingProtocol.Lifetime, PollInterval, ActivationLook.Interval); PrivacyPageTests confere-os.
    // Texto do plano (0.2.15, §3.5) com as variantes do código: «sincronizar agora» na lista; «Sair deste computador» fora
    // dela, porque não faz nenhum pedido (a rota /sync-google-out não revoga). Acrescentadas por serem pedidos imediatos
    // que o plano não listava: «gerar um kit novo» (troca a chave da conta no Drive, /sync-new-code) e ligar uma linha do
    // tópico ou aceitar a oferta de pagamentos e documentos (fazem logo uma ronda, SyncService.SyncNow).
    public const string SyncException =
        "Exceção que escolheu: com sessão iniciada na Google para a sincronização, o Éteris fala com a Google por um cliente próprio (não pelo motor). "
        + "Com o Google Drive (só a pasta oculta da aplicação, tudo cifrado neste computador): 30 s depois de abrir (ou 10 s depois de uma alteração, se for antes), "
        + "10 s depois de uma alteração, de 10 em 10 minutos, e quando abre o menu do Éteris (⋯), volta à janela do Éteris (no máximo uma vez a cada 5 minutos, "
        + "ou logo se uma ronda já tiver encontrado um pedido) ou tem a secção Sincronização das Definições à vista, para ver pedidos de outros computadores "
        + "(ao responder a um, acompanha-o de 2 em 2 s até acabar, no máximo 10 minutos). "
        + "Com oauth2.googleapis.com, para renovar a sessão quando expira. Uma vez, para saber o nome e o e-mail da conta. "
        + "E nas ações que pedir: entrar, sincronizar agora, aprovar ou recusar um computador, remover um aparelho, gerar um kit novo, "
        + "ligar uma linha de «O que passa entre PCs» ou aceitar pagamentos e documentos, apagar os dados do Drive e "
        + "retirar o acesso do Éteris à conta Google (que desliga todos os computadores). Sair deste computador não fala com a Google.";

    // O que a Google vê (plano §3.5, à letra). Fecha a exceção na promessa «no-google» e está no bloco de sincronização.
    public const string SyncSees =
        "A Google vê que usa o Éteris, quando sincroniza, quantos ficheiros há e o tamanho aproximado deles. Não vê o que está lá dentro.";

    // A chave (plano §3.5), com a condição real do Windows ao aprovar: SyncChoices.MayApproveAsync só o pede com segredos
    // em jogo (agent.HasSecrets). Sem marcação: a página escreve-a com textContent.
    public const string SyncKey =
        "A chave da sincronização é criada no seu computador e nunca vai para a Google nem para o projeto Éteris. "
        + "Para pôr outro computador na conta, aprova-o num computador que já esteja ligado (com o Windows Hello, quando há palavras-passe ou outros dados do cofre em jogo) "
        + "ou usa o seu kit de recuperação. Sem nenhum computador ligado e sem o kit, a cópia na nuvem não se recupera, por ninguém.";

    // O que vai (plano §3.6 (2), à letra).
    public const string SyncGoes =
        "Favoritos, Espaços, definições, decisões do Escudo, notas, palavras-passe, contactos e viagens; pagamentos e documentos de identificação só se os ligar. "
        + "O histórico, as abas abertas, cookies, cache e permissões dos sites não vão.";

    // O que a sincronização guarda neste computador além da conta: um registo curto das falhas (SyncLog). Sem marcação.
    public const string SyncLocal =
        "Neste computador a sincronização guarda também um registo curto das falhas (sync\\sync.log): só a hora, um texto fixo e o tipo da falha, nunca dados, nomes de ficheiros nem caminhos. "
        + "Apaga-se ao «Sair deste computador».";

    // PROPOSTA (título do bloco; o dono aprova).
    public const string SyncBlockTitle = "Se ligar a sincronização";

    // O bloco «Se ligar a sincronização» de eteris://privacy/#sincronizacao: sempre igual (com ou sem sessão, com a
    // sincronização ligada ou desligada, também numa janela privada), para quem ainda não entrou poder ler o que vai ser
    // enviado. Não é uma promessa (nada nele se mede) e não faz pedidos.
    public static object SyncBlock => new { title = SyncBlockTitle, goes = SyncGoes, sees = SyncSees, key = SyncKey, local = SyncLocal };

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public static State Current()
    {
        var (visible, hidden) = Infrastructure.CaptureShield.Count();
        var settings = Data.Settings.Current;
        var bounces = MainWindow.Bounces;
        var args = Environment.GetCommandLineArgs();
        return new(EngineHardening.Applied, PrivacySignals.Headers, PrivacySignals.ScriptFor("https://example.com/") is not null,
            PrivacyPreferences.ThirdPartyCookiesBlocked, PrivacyPreferences.WebRtcIpProtected, PrivacyPreferences.SecureDnsProvider,
            EngineHardening.StartedWithLanguage, settings.HideFromCapture, visible, hidden,
            settings.SkipTrackingRedirects, settings.ForgetBounceTrackers, bounces.Cleared, bounces.PendingCount,
            settings.UncloakCname, CnameStats.BlockedCount, CnameStats.DecisionCount, CnameStats.MicrosPerDecision, Cname.SourceFor(isPrivate: false),
            Sync.SyncService.Enabled || (!Sync.SyncService.FolderMode && Sync.GoogleSync.SignedIn), PrivacyPreferences.LeakCheckOff,
            PrivacyPreferences.ChromiumPasswordsOff,
            Infrastructure.SandboxFallback.IsolationOf(Infrastructure.CefConfigurator.SandboxSupported, Infrastructure.SandboxInfo.Available != 0,
                Infrastructure.CefConfigurator.SandboxActive, Infrastructure.StartupGuard.Current.Notice, Infrastructure.SandboxInfo.Disabled(args),
                // O motor já não lê o --no-sandbox do Chromium (CommandLineArgsDisabled, INF-09): só o do Éteris conta.
                engineNoSandbox: false),
            Infrastructure.NetworkSandbox.Enabled, Infrastructure.StartupGuard.Current.ReenableRequested,
            Shields.ShieldEngine.Cosmetic is not null, settings.HttpsFirst);
    }

    // Depois de «Apagar o relatório»: volta à página (sem script, a CSP da página não deixa) e sem repetir o pedido.
    public const string ReportClearedRedirect =
        "<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url=eteris://privacy/#relatorio\"><title>Relatório apagado</title>";

    // Depois de «Voltar a ligar»: volta à página (sem script) e sem repetir o pedido.
    public const string SandboxRetryRedirect =
        "<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url=eteris://privacy/\"><title>Isolamento das páginas</title>";

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
            promises = promises.Select(p => new
            {
                id = p.Id, title = p.Title, detail = p.Detail, kept = p.Kept, live = p.Live,
                // A nova tentativa do sandbox muda estado: leva o token da página interna (InternalPages.IsAuthorized).
                fix = p.Fix == SandboxRetryUrl ? p.Fix + "?t=" + InternalPages.Token : p.Fix, fixLabel = p.FixLabel,
            }),
            report = ReportJson(events, today ?? DateOnly.FromDateTime(DateTime.Today)),
            sync = SyncBlock,
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
