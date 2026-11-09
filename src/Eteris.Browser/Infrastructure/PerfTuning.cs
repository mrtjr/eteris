namespace Eteris.Browser.Infrastructure;

// Um ajuste de desempenho do plano da 0.2.14 (fase 1): o que muda, se está ligado por omissão e porquê não.
//   Id               a referência da experiência no plano (e1, e3…), também o nome do interruptor;
//   BeforeFirstPaint mexe no caminho antes da 1.ª página (regra R-2 do sandbox): fica desligado por omissão até a rede de
//                    segurança saber desligar os ajustes antes do sandbox (D-18, gancho Suspend);
//   Waits            a decisão do dono ou o diagnóstico de que depende (D-4, D4…); com ela, desligado por omissão.
public sealed record PerfTweak(string Id, string What, bool OnByDefault, bool BeforeFirstPaint = false, string? Waits = null);

// Interruptores dos ajustes de desempenho, para os desligar (ou ligar, nas medições) sem compilar de novo:
//
//   --eteris-sem-ajustes                  todos desligados (recuo; vale também numa instalação, por um atalho);
//   --eteris-ajustes=e3,-e5               liga o e3 e desliga o e5 (também «tudo» e «nada»);
//   ETERIS_TEST_AJUSTES=e3,-e5            o mesmo, para os braços A/B das medições (build/perf); inerte numa instalação,
//                                         como os outros ETERIS_TEST_* (Infrastructure/PerfDiagnostics.cs).
//
// O argumento ganha à variável, e o --eteris-sem-ajustes a tudo. O E2 (manifesto do Windows 10/11) não está aqui: é um
// recurso do exe, fixo na compilação. Lê-se uma vez (a linha de comandos e uma variável do ambiente; nada no disco sem a
// variável) e daí em diante é um campo.
public static class PerfTuning
{
    public const string AllOffArgument = "--eteris-sem-ajustes";
    public const string ArgumentPrefix = "--eteris-ajustes=";
    public const string TestVariable = "ETERIS_TEST_AJUSTES";

    public const string WaitingDot = "e1";
    public const string ShaderCache = "e3";
    public const string MediaPolling = "e4";
    public const string OwnLinkBubble = "e5";
    public const string ReleaseGlance = "e6";
    public const string SmallBackForwardCache = "e7";
    public const string HideWhenUnseen = "e8";

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
            OnByDefault: false, BeforeFirstPaint: true, Waits: "D-5"),
        new(HideWhenUnseen, "a página à vista deixa de desenhar com a janela minimizada, noutra área de trabalho ou com a sessão bloqueada (nunca com som, multimédia, captura ou Picture-in-Picture)",
            OnByDefault: false, Waits: "D4"),
        new(OwnLinkBubble, "a bolha do link numa janela do tamanho do texto e o contorno do ecrã dividido na janela principal, fora da camada do tamanho da janela",
            OnByDefault: true),
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

    // Os ligados para esta linha de comandos e este valor da variável de teste (já nulo numa instalação).
    public static IReadOnlySet<string> Resolve(IEnumerable<string> args, string? testValue)
    {
        var list = args.ToList();
        if (list.Any(a => a.Trim().Equals(AllOffArgument, StringComparison.OrdinalIgnoreCase))) return new HashSet<string>(StringComparer.Ordinal);
        var on = new HashSet<string>(Defaults, StringComparer.Ordinal);
        Apply(on, testValue);
        foreach (var arg in list.Where(a => a.Trim().StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase)))
            Apply(on, arg.Trim()[ArgumentPrefix.Length..]);
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
            else if (item.StartsWith('-') && Known(item[1..])) on.Remove(item[1..]);
            else if (item.StartsWith('+') && Known(item[1..])) on.Add(item[1..]);
            else if (Known(item)) on.Add(item);
        }
    }

    private static bool Known(string id) => Tweaks.Any(t => t.Id == id);

    private static string? Read(string variable)
        => Environment.GetEnvironmentVariable(variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
