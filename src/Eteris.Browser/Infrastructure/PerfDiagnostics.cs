using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CefSharp.Wpf.HwndHost;

namespace Eteris.Browser.Infrastructure;

// Camadas do Éteris por cima do motor que o braço «puro» das medições desliga (ETERIS_TEST_PURO; plano de desempenho
// da 0.2.14, D5): se o «puro» ficar ao nível do Chrome, a diferença de CPU é destas camadas; se não, é do motor.
[Flags]
public enum PerfLayers
{
    None = 0,
    // Sessões DevTools por aba (scriptlets, GPC, dicas reduzidas), o Runtime.evaluate do PageScript e as miniaturas.
    DevTools = 1,
    // A sondagem da central de multimédia (Runtime.evaluate em todas as abas de 2 em 2 s).
    Media = 2,
    // A janela em camadas por cima da página (bolha do link, avisos, contorno do ecrã dividido).
    Overlay = 4,
    // O Mica da moldura (DWM).
    Mica = 8,
    // As animações eternas do WPF (o ponto «a tocar» da central de multimédia).
    Animations = 16,
    All = DevTools | Media | Overlay | Mica | Animations,
}

// Interruptores SÓ para as medições de desempenho (build/perf/README.md), lidos do ambiente do processo que lança o
// Éteris. Como a pasta de teste da sincronização, uma instalação ignora-os todos (há um Update.exe do Velopack na pasta
// de cima): uma variável esquecida no PC de alguém nunca muda o Éteris instalado. Mede-se sempre a pasta da publicação
// (build/publish.ps1) como cópia portátil, num perfil de teste.
//
//   ETERIS_TEST_PURO=1                     todas as camadas de PerfLayers desligadas;
//   ETERIS_TEST_PURO=devtools,midia,...    só essas (devtools, midia, camada, mica, animacoes);
//   ETERIS_TEST_CONTADORES=1|<caminho>     contadores dos mecanismos por segundo (Infrastructure/PerfCounters.cs);
//   ETERIS_TEST_HOST_RULES=<regras>        nomes de teste mapeados para 127.0.0.1 (--host-resolver-rules), só
//                                          «MAP <nome>.example|.test|.invalid|.localhost 127.0.0.1[:porta]».
//   ETERIS_TEST_PRESSAO=1                  sinal simulado de pouca memória para o sono por pressão (ajuste e13,
//                                          Tabs/MemoryPressure.cs), em vez de reservar ~25 GB (decisão D-16).
//
// Fora do caminho antes da 1.ª pintura (regra R-2 do plano do sandbox): sem as variáveis, a 1.ª consulta lê o ambiente
// (nada no disco) e as seguintes são um campo estático; o HOST_RULES só acrescenta um argumento à linha de comandos do
// motor quando a variável existe.
public static class PerfDiagnostics
{
    public const string PureVariable = "ETERIS_TEST_PURO";
    public const string CountersVariable = "ETERIS_TEST_CONTADORES";
    public const string HostRulesVariable = "ETERIS_TEST_HOST_RULES";
    public const string PressureVariable = "ETERIS_TEST_PRESSAO";

    private static readonly Lazy<bool> Installed = new(() => DetectInstalled(AppContext.BaseDirectory));
    // Sem a variável não se olha sequer para o disco (a pasta da instalação): só a leitura do ambiente.
    private static readonly Lazy<PerfLayers> Pure = new(() => Read(PureVariable) is { } value ? ParsePure(value, Installed.Value) : PerfLayers.None);
    private static readonly Lazy<string?> Rules = new(() => Read(HostRulesVariable) is { } value ? ParseHostRules(value, Installed.Value) : null);
    private static readonly Lazy<bool> Pressure = new(() => Read(PressureVariable) is { } value && ParsePressure(value, Installed.Value));

    private static string? Read(string variable)
        => Environment.GetEnvironmentVariable(variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    // Mesma regra que SyncService.IsInstalled (ramo sync): a instalação do Velopack tem o Update.exe na pasta de cima.
    public static bool IsInstalled => Installed.Value;

    public static bool DetectInstalled(string baseDirectory)
        => File.Exists(Path.Combine(baseDirectory, "..", "Update.exe"));

    public static PerfLayers PureLayers => Pure.Value;

    // A camada está desligada pelo braço «puro».
    public static bool Without(PerfLayers layer) => (Pure.Value & layer) != 0;

    public static string? HostRules => Rules.Value;

    // Pouca memória simulada (ETERIS_TEST_PRESSAO=1): o sono por pressão age como se o Windows a assinalasse.
    public static bool SimulatedPressure => Pressure.Value;

    public static bool ParsePressure(string? value, bool installed) => !installed && value?.Trim() == "1";

    public static PerfLayers ParsePure(string? value, bool installed)
    {
        if (installed || string.IsNullOrWhiteSpace(value)) return PerfLayers.None;
        var text = value.Trim();
        if (text == "1") return PerfLayers.All;
        var layers = PerfLayers.None;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            layers |= part.ToLowerInvariant() switch
            {
                "devtools" => PerfLayers.DevTools,
                "midia" or "mídia" => PerfLayers.Media,
                "camada" => PerfLayers.Overlay,
                "mica" => PerfLayers.Mica,
                "animacoes" or "animações" => PerfLayers.Animations,
                _ => PerfLayers.None,
            };
        }
        return layers;
    }

    // Caminho do ficheiro dos contadores: «1» = %TEMP%\eteris-contadores-<pid>.tsv; outro valor é o caminho ({pid} é
    // trocado pelo processo). Nulo sem a variável ou numa instalação.
    public static string? ResolveCountersPath(string? value, int processId, bool installed) => installed ? null : value?.Trim() switch
    {
        null or "" or "0" => null,
        "1" => Path.Combine(Path.GetTempPath(), $"eteris-contadores-{processId}.tsv"),
        var path => path.Replace("{pid}", processId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
    };

    public static string? CountersPath
        => Read(CountersVariable) is { } value ? ResolveCountersPath(value, Environment.ProcessId, Installed.Value) : null;

    // Só nomes reservados para testes (RFC 2606/6761) e só para o próprio PC: uma regra que mandasse um site real para
    // outro endereço seria um desvio de tráfego. Uma regra inválida desliga a variável inteira (não meia lista).
    private static readonly Regex HostRule = new(
        @"^MAP\s+(\*\.)?([a-z0-9-]+\.)*[a-z0-9-]+\.(example|test|invalid|localhost)\s+127\.0\.0\.1(:\d{1,5})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? ParseHostRules(string? value, bool installed)
    {
        if (installed || string.IsNullOrWhiteSpace(value)) return null;
        var rules = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rules.Length == 0 || rules.Any(r => !HostRule.IsMatch(r))) return null;
        return string.Join(',', rules.Select(r => Regex.Replace(r, @"\s+", " ")));
    }

    // Linha de comandos do motor (CefConfigurator.Prepare, depois do Build): só com a variável.
    public static void ConfigureEngine(CefSettings settings)
    {
        if (HostRules is { } rules) settings.CefCommandLineArgs["host-resolver-rules"] = rules;
    }
}
