namespace Eteris.Browser.Infrastructure;

// Um ajuste de desempenho do plano da 0.2.14 (fases 1 e 2): o que muda, se está ligado por omissão e porquê não.
//   Id               a referência da experiência no plano (e1, e3…, e14.1 para a 1.ª entrada do E14), também o nome do
//                    interruptor;
//   BeforeFirstPaint mexe no caminho antes da 1.ª página (regra R-2 do sandbox): fica desligado por omissão até a rede de
//                    segurança saber desligar os ajustes antes do sandbox (D-18, gancho Suspend);
//   Waits            a decisão do dono ou o diagnóstico de que depende (D-4, D4…); com ela, desligado por omissão;
//   Engine           muda a configuração do motor (Shields/EnginePerformance.cs, ou o Local State no e16): o grupo
//                    «motor» e o --eteris-sem-ajustes-motor ligam-nos e desligam-nos todos de uma vez.
public sealed record PerfTweak(string Id, string What, bool OnByDefault, bool BeforeFirstPaint = false, string? Waits = null,
                               bool Engine = false);

// Interruptores dos ajustes de desempenho, para os desligar (ou ligar, nas medições) sem compilar de novo:
//
//   --eteris-sem-ajustes                  todos desligados (recuo; vale também numa instalação, por um atalho);
//   --eteris-sem-ajustes-motor            só os do motor desligados (o grupo «motor»: E7, E14, E15, E16), os outros como
//                                         estiverem: o recuo de uma funcionalidade do motor que falhe, sem perder o resto;
//   --eteris-ajustes=e3,-e5               liga o e3 e desliga o e5; também «tudo», «nada», «motor» (os do motor) e o
//                                         número de uma experiência para as suas entradas («e14» = e14.1…e14.6);
//   ETERIS_TEST_AJUSTES=e3,-e5            o mesmo, para os braços A/B das medições (build/perf); inerte numa instalação,
//                                         como os outros ETERIS_TEST_* (Infrastructure/PerfDiagnostics.cs).
//
// O argumento ganha à variável, o --eteris-sem-ajustes-motor aos dois (nos do motor) e o --eteris-sem-ajustes a tudo. O
// E2 (manifesto do Windows 10/11) não está aqui: é um recurso do exe, fixo na compilação. Lê-se uma vez (a linha de
// comandos e uma variável do ambiente; nada no disco sem a variável) e daí em diante é um campo.
public static class PerfTuning
{
    public const string AllOffArgument = "--eteris-sem-ajustes";
    public const string EngineOffArgument = "--eteris-sem-ajustes-motor";
    public const string EngineGroup = "motor";
    public const string ArgumentPrefix = "--eteris-ajustes=";
    public const string TestVariable = "ETERIS_TEST_AJUSTES";

    public const string WaitingDot = "e1";
    public const string ShaderCache = "e3";
    public const string MediaPolling = "e4";
    public const string OwnLinkBubble = "e5";
    public const string ReleaseGlance = "e6";
    public const string SmallBackForwardCache = "e7";
    public const string HideWhenUnseen = "e8";
    public const string UpdatesOnExitOnly = "e10";
    public const string EngineThreadStartup = "e11";
    public const string ReadyNewTab = "e12";
    public const string SleepUnderPressure = "e13";
    // Fase 2 (sem recompilar o motor): funcionalidades que o Chrome estável recebe por variações e a nossa CEF nunca liga
    // (Shields/EnginePerformance.cs, com o que faz cada uma, o risco, a medição e a prova no Chromium 156), e a poupança de
    // energia do próprio Chromium (Infrastructure/EnergySaver.cs).
    public const string NoDamageThrottle = "e14.1";
    public const string BeginFrameRunOrPost = "e14.2";
    public const string SingleVideoThrottle = "e14.3";
    public const string GpuPersistentCache = "e14.4";
    public const string DelegatedCompositing = "e14.5";
    public const string SkiaGraphite = "e14.6";
    public const string HiddenWebGlBuffers = "e15.1";
    public const string KillSpareOnPressure = "e15.2";
    public const string SpareCriticalPressure = "e15.3";
    public const string SortActiveSlotSpans = "e15.4";
    public const string V8SqrtLimits = "e15.5";
    public const string EnergySaver = "e16";

    public static readonly IReadOnlyList<PerfTweak> Tweaks =
    [
        new(WaitingDot, "o ponto «a tocar» da central de multimédia só anima enquanto se vê", OnByDefault: true),
        new(ShaderCache, "cache dos shaders da GPU no disco (apagada com os dados dos sites e depois de uma janela privada)",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D-4"),
        new(MediaPolling, "a central de multimédia sonda de 2 em 2 s só as abas à vista, quentes ou com som, e as outras de 30 em 30 s; o modo foco deixa de ler o rato com a janela inativa",
            OnByDefault: true),
        new(ReleaseGlance, "o Espreitar liberta o motor 60 s depois de fechar; os sites fixados escondidos na barra lateral adormecem como as abas (salvo «Manter acordado»)",
            OnByDefault: true),
        new(SmallBackForwardCache, "Voltar/Avançar instantâneo para as 3 últimas páginas de cada aba (o Chromium guarda 6): menos renderers vivos",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D-5", Engine: true),
        new(HideWhenUnseen, "a página à vista deixa de desenhar com a janela minimizada, noutra área de trabalho ou com a sessão bloqueada (nunca com som, multimédia, captura ou Picture-in-Picture)",
            OnByDefault: false, Waits: "D4"),
        new(UpdatesOnExitOnly, "as atualizações aplicam-se só ao sair do Éteris, nunca no arranque", OnByDefault: false,
            BeforeFirstPaint: true, Waits: "D-7"),
        new(EngineThreadStartup, "o motor prepara-se (pastas, limpeza do perfil, Local State, definições) e arranca na sua thread logo a seguir à instância única, em paralelo com a interface",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "E9"),
        new(ReadyNewTab, "a 1.ª nova aba prepara-se em fundo durante o arranque; as definições e a sessão leem-se sem reflexão",
            OnByDefault: false, BeforeFirstPaint: true),
        new(SleepUnderPressure, "com pouca memória, adormece uma aba de cada vez (a usada há mais tempo, com as isenções) e a rolagem volta ao acordar",
            OnByDefault: false, Waits: "D-6"),
        new(OwnLinkBubble, "a bolha do link numa janela do tamanho do texto e o contorno do ecrã dividido na janela principal, fora da camada do tamanho da janela",
            OnByDefault: true),
        // Fase 2. Todas desligadas por omissão até ao seu A/B, uma de cada vez pela ordem do plano: mudam a configuração do
        // motor, que entra antes da 1.ª página (R-2), e as de GPU podem fazer cair o processo da GPU antes dela (D-18).
        new(NoDamageThrottle, "o motor abranda os fotogramas de uma página que pede animação sem mudar nada (depois de 360 seguidos)",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D3/D5", Engine: true),
        new(BeginFrameRunOrPost, "o vsync do Windows começa o fotograma sem um salto de tarefa (mexe na latência; CPU ~0)",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D3/D5", Engine: true),
        new(SingleVideoThrottle, "uma página só com um vídeo de 30 fps compõe a 30 Hz em vez de 60",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D3/D5", Engine: true),
        new(GpuPersistentCache, "cache persistente dos shaders da GPU (só com o e3, a cache dos shaders no disco)",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D3/D5", Engine: true),
        new(DelegatedCompositing, "composição delegada parcial (limit_to_ui); no Éteris espera-se 0 ou pior: opcional, depois das outras",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D3/D5", Engine: true),
        new(SkiaGraphite, "o Skia Graphite (Dawn sobre D3D11) desenha as páginas em vez do Ganesh; muda o resultado do canvas, isto é, a impressão digital",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D-3", Engine: true),
        new(HiddenWebGlBuffers, "o WebGL e o OffscreenCanvas das abas escondidas largam o back buffer na GPU",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D9", Engine: true),
        new(KillSpareOnPressure, "com pouca memória, o motor fecha o renderer de reserva",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D9", Engine: true),
        new(SpareCriticalPressure, "o renderer de reserva só se deixa de criar (ou se fecha) com pressão de memória crítica, não já com a moderada",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D9", Engine: true),
        new(SortActiveSlotSpans, "o PartitionAlloc também ordena os blocos ativos quando purga a memória",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D9", Engine: true),
        new(V8SqrtLimits, "os limites do heap do V8 crescem pela raiz quadrada do tamanho (menos memória nas páginas grandes)",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D9", Engine: true),
        new(EnergySaver, "a poupança de energia do próprio Chromium (tudo a 30 fps, despertares alinhados), com o estado escrito no Local State antes de o motor arrancar",
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D-8", Engine: true),
    ];

    private static readonly Lazy<IReadOnlySet<string>> Resolved = new(()
        => Resolve(Environment.GetCommandLineArgs(), Read(TestVariable) is { } value && !PerfDiagnostics.IsInstalled ? value : null));

    private static volatile string? _suspended;

    // Gancho da rede de segurança do sandbox (D-18, por fundir: o StartupGuard está no ramo do sandbox). A 1.ª queda antes
    // da 1.ª página desliga os ajustes no arranque seguinte (chamar isto antes de o motor arrancar); só a 2.ª desliga o
    // sandbox. Daí em diante IsOn é falso para todos.
    public static void Suspend(string reason) => _suspended = string.IsNullOrWhiteSpace(reason) ? "rede de segurança" : reason;

    public static string? SuspendedBecause => _suspended;

    public static bool IsOn(string id) => _suspended is null && Resolved.Value.Contains(id);

    // Os ligados neste processo (eteris://privacy/ e o rastro do arranque).
    public static IReadOnlySet<string> On => _suspended is null ? Resolved.Value : new HashSet<string>();

    public static IReadOnlySet<string> Defaults { get; } = Tweaks.Where(t => t.OnByDefault).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

    // Os ajustes que mudam a configuração do motor (o grupo «motor»).
    public static IReadOnlyList<string> EngineIds { get; } = Tweaks.Where(t => t.Engine).Select(t => t.Id).ToList();

    // Os ligados para esta linha de comandos e este valor da variável de teste (já nulo numa instalação).
    public static IReadOnlySet<string> Resolve(IEnumerable<string> args, string? testValue)
    {
        var list = args.ToList();
        if (list.Any(a => a.Trim().Equals(AllOffArgument, StringComparison.OrdinalIgnoreCase))) return new HashSet<string>(StringComparer.Ordinal);
        var on = new HashSet<string>(Defaults, StringComparer.Ordinal);
        Apply(on, testValue);
        foreach (var arg in list.Where(a => a.Trim().StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase)))
            Apply(on, arg.Trim()[ArgumentPrefix.Length..]);
        if (list.Any(a => a.Trim().Equals(EngineOffArgument, StringComparison.OrdinalIgnoreCase))) on.ExceptWith(EngineIds);
        return on;
    }

    private static void Apply(HashSet<string> on, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = raw.ToLowerInvariant();
            if (item == "tudo") on.UnionWith(Tweaks.Select(t => t.Id));
            else if (item == "nada") on.Clear();
            else if (item.StartsWith('-')) on.ExceptWith(Expand(item[1..]));
            else if (item.StartsWith('+')) on.UnionWith(Expand(item[1..]));
            else on.UnionWith(Expand(item));
        }
    }

    // Um ajuste («e14.1»), as entradas de uma experiência («e14» = e14.1…e14.6) ou «motor» (todos os do motor). Um nome
    // desconhecido não faz nada.
    public static IEnumerable<string> Expand(string item)
    {
        if (item == EngineGroup) return EngineIds;
        if (Tweaks.Any(t => t.Id == item)) return [item];
        return Tweaks.Select(t => t.Id).Where(id => id.StartsWith(item + ".", StringComparison.Ordinal)).ToList();
    }

    private static string? Read(string variable)
        => Environment.GetEnvironmentVariable(variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
