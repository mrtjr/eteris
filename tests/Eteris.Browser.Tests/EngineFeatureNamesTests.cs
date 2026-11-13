using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Xunit.Abstractions;

namespace Eteris.Browser.Tests;

// Revisão aos 50: um nome em enable-features/disable-features que não é uma base::Feature do motor é ignorado em
// silêncio — e a promessa em eteris://privacy/ ficava verde por causa de um interruptor que não existe. Cada nome que o
// Éteris põe na linha de comandos (as listas e tudo o que o CefConfigurator.Build lá deixa) tem de ter, no libcef.dll
// do NuGet e, quando existe, do da compilação própria (build/cef-codecs/out/runtime), a estrutura base::Feature: um
// ponteiro para o nome (no CEF 156 a cadeia é «kNome» e o ponteiro aponta para o «N»; nas versões antigas, para
// «Nome») seguido do estado por omissão (0 ou 1). Cada caso diz no nome o motor que examinou.
public class EngineFeatureNamesTests(ITestOutputHelper output)
{
    // O motor que o NuGet do CefSharp põe na saída dos testes.
    internal const string TestOutput = "saída dos testes";

    // O motor com codecs, compilado na máquina de publicação (docs/motor.md); não existe no CI nem num checkout novo.
    internal const string OwnBuild = "build/cef-codecs/out/runtime";

    // Nomes que o próprio teste distingue como não sendo base::Feature: os três primeiros só existem como
    // funcionalidades do Blink (ou numa tabela de nomes); os cinco seguintes estiveram na linha de comandos até à
    // auditoria e o Chromium 156 ignorava-os.
    private static readonly string[] Dead =
    [
        "PartitionedCookies", "AttributionReporting", "SharedStorageAPI",
        "DialMediaRouteProvider", "CastMediaRouteProvider", "OptimizationHintsFetching",
        "OptimizationGuideModelDownloading", "OptimizationGuideOnDeviceModel",
    ];

    // O que se leu de cada motor: as funcionalidades (com o(s) estado(s) por omissão) e as flags do V8 presentes. Os
    // bytes do ficheiro largam-se no fim de cada leitura: nunca ficam dois libcef.dll inteiros em memória.
    private sealed record EngineScan(Dictionary<string, HashSet<int>> Features, HashSet<string> V8Flags);

    private static readonly ConcurrentDictionary<string, Lazy<EngineScan>> Scans = new(StringComparer.Ordinal);

    private static EngineScan ScanOf(string engine) => Scans.GetOrAdd(engine, e => new Lazy<EngineScan>(() => Scan(PathOf(e)))).Value;

    internal static IReadOnlyList<string> Engines()
        => File.Exists(PathOf(OwnBuild)) ? [TestOutput, OwnBuild] : [TestOutput];

    private static string PathOf(string engine) => engine switch
    {
        TestOutput => Path.Combine(AppContext.BaseDirectory, "libcef.dll"),
        OwnBuild => Path.Combine(Root(), "build", "cef-codecs", "out", "runtime", "libcef.dll"),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
    };

    private static string Root([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    // Sem o motor próprio só se examina o do NuGet: o teste di-lo na saída, e cada caso leva o motor no nome.
    [Fact]
    public void Engines_IncludeTheOwnBuild_WhenItExists()
    {
        var engines = Engines();
        Assert.Contains(TestOutput, engines);
        Assert.Equal(File.Exists(PathOf(OwnBuild)), engines.Contains(OwnBuild));
        output.WriteLine("Motores examinados: " + string.Join(", ", engines.Select(e => $"«{e}»")));
        if (!engines.Contains(OwnBuild))
            output.WriteLine($"Sem a compilação própria ({OwnBuild}/libcef.dll): só o motor do NuGet foi examinado.");
    }

    private static IEnumerable<string> FeatureNames()
    {
        var args = CefConfigurator.Build("p", networkSandbox: true).CefCommandLineArgs.ToDictionary();
        var onTheCommandLine = new[] { "disable-features", "enable-features" }.SelectMany(s => EngineHardening.Switch(args, s));
        return EngineHardening.AdApis.Concat(EngineHardening.Partitioning).Concat(EngineHardening.LocalNetworkChecks)
            .Concat(EngineHardening.Identity).Concat(EngineHardening.NoListeningSockets).Concat(EngineHardening.NoGoogleAtStartup)
            .Concat(EngineHardening.NoGoogleOnPages)
            .Select(f => f.Feature).Append("HttpsUpgrades").Append(NetworkSandbox.Feature)
            .Concat(EnginePerformance.Enabled.Select(f => f.Feature))
            .Concat(EngineAppearance.All.Select(f => f.Feature))
            .Concat(CefConfigurator.Build("p", overlayScrollbars: true).CefCommandLineArgs.ToDictionary()
                .Where(a => a.Key is "enable-features" or "disable-features")
                .SelectMany(a => a.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Concat(onTheCommandLine)
            // As de desempenho levam parâmetros («Nome:param/valor»): procura-se só o nome. As «V8Flag_x» não são
            // base::Feature (o gin passa-as ao V8 como --x): têm o seu teste, mais abaixo.
            .Select(EnginePerformance.FeatureName)
            .Where(n => !n.StartsWith(EnginePerformance.V8FlagPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> V8FlagNames()
        => EnginePerformance.Enabled.Select(f => EnginePerformance.FeatureName(f.Feature))
            .Where(n => n.StartsWith(EnginePerformance.V8FlagPrefix, StringComparison.Ordinal))
            .Select(n => n[EnginePerformance.V8FlagPrefix.Length..]);

    // Cada caso, uma vez por motor, com o rótulo do motor como 1.º argumento (aparece no nome do caso).
    private static IEnumerable<object[]> PerEngine(IEnumerable<object[]> cases)
    {
        var list = cases.ToList();
        return Engines().SelectMany(engine => list.Select(c => c.Prepend(engine).ToArray()));
    }

    public static IEnumerable<object[]> Names() => PerEngine(FeatureNames().Select(n => new object[] { n }));

    [Theory]
    [MemberData(nameof(Names))]
    public void EverySwitchedFeature_IsABaseFeatureOfTheEngine(string engine, string name)
        => Assert.True(ScanOf(engine).Features.ContainsKey(name),
            $"«{name}» não é uma base::Feature do libcef.dll ({engine}): o Chromium ignorava-o");

    // Fase 2 do plano de desempenho: as funcionalidades confirmadas na árvore do Chromium 156 (cc/base/features.cc,
    // components/viz/common/features.cc, external_begin_frame_source_win.cc, gpu/config/gpu_finch_features.cc,
    // third_party/blink/common/features.cc, spare_render_process_host_manager_impl.cc, partition_alloc_features.cc) têm de
    // estar também no libcef.dll do NuGet e, quando existe, no da compilação própria (build/cef-codecs/out/runtime), e
    // com o estado por omissão que o plano conta (todas desligadas, salvo a cache do Graphite, ligada no Windows: é por
    // isso que o Éteris a desliga sem o e3).
    public static IEnumerable<object[]> PhaseTwo() => PerEngine(
    [
        ["ThrottleRepeatedNoDamageFrames", 0],
        ["ExternalBeginFrameSourceWinUsesRunOrPostTask", 0],
        ["SingleVideoFrameRateThrottling", 0],
        ["GpuPersistentCache", 0],
        ["GpuPersistentCacheMetadata", 0],
        ["DelegatedCompositing", 0],
        ["SkiaGraphite", 0],
        ["SkiaGraphiteUsePersistentCache", 1],
        ["WebGLDiscardBackBuffer", 0],
        ["OffscreenCanvasPropagateVisibility", 0],
        ["KillSpareRenderOnMemoryPressure", 0],
        ["SpareRPHUseCriticalMemoryPressure", 0],
        ["PartitionAllocSortActiveSlotSpans", 0],
    ]);

    [Theory]
    [MemberData(nameof(PhaseTwo))]
    public void PhaseTwoFeatures_HaveTheDefaultThePlanCountsOn(string engine, string name, int state)
    {
        Assert.Contains(EnginePerformance.Enabled, f => EnginePerformance.FeatureName(f.Feature) == name);
        Assert.True(ScanOf(engine).Features.TryGetValue(name, out var found), $"«{name}» não é uma base::Feature do libcef.dll ({engine})");
        Assert.Contains(state, found);
    }

    // «V8Flag_x»: a flag «x» do V8 tem de existir no binário (o V8 guarda o nome de cada flag numa tabela de cadeias);
    // uma flag que não existe era ignorada (o V8 avisa, mas na consola de um processo sem consola).
    public static IEnumerable<object[]> V8Flags() => PerEngine(V8FlagNames().Select(n => new object[] { n }));

    [Theory]
    [MemberData(nameof(V8Flags))]
    public void EveryV8Flag_IsAFlagOfTheEngine(string engine, string flag)
        => Assert.True(ScanOf(engine).V8Flags.Contains(flag), $"«{flag}» não é uma flag do V8 no libcef.dll ({engine}): o V8 ignorava-a");

    // Proposta 3 («Pele nova»): as funcionalidades de aspeto (Shields/EngineAppearance.cs), com o estado por omissão
    // que a decisão conta — as barras sobrepostas desligadas (a opção liga-as) e o «flash» ao entrar com o rato ligado —
    // e a RootScrollbarFollowsBrowserTheme, que fica de fora: vem desligada no motor, por isso não é ela que pinta a barra
    // da raiz com o tema do Windows (3.3 (3b)).
    public static IEnumerable<object[]> Appearance() => PerEngine(
    [
        ["OverlayScrollbar", 0],
        ["OverlayScrollbarFlashWhenMouseEnter", 1],
        ["RootScrollbarFollowsBrowserTheme", 0],
    ]);

    [Theory]
    [MemberData(nameof(Appearance))]
    public void AppearanceFeatures_HaveTheDefaultTheDecisionCountsOn(string engine, string name, int state)
    {
        Assert.True(ScanOf(engine).Features.TryGetValue(name, out var found), $"«{name}» não é uma base::Feature do libcef.dll ({engine})");
        Assert.Equal([state], found);
        Assert.Equal(name != "RootScrollbarFollowsBrowserTheme", EngineAppearance.All.Any(f => f.Feature == name));
    }

    // As barras Fluent não existem no motor 156: um nome destes seria ignorado em silêncio (e nunca entra nas listas).
    private static readonly string[] Absent = ["FluentScrollbar", "FluentOverlayScrollbar"];

    public static IEnumerable<object[]> AbsentNames() => PerEngine(Absent.Select(n => new object[] { n }));

    [Theory]
    [MemberData(nameof(AbsentNames))]
    public void FluentScrollbars_AreNotInThisEngine(string engine, string name)
    {
        Assert.False(ScanOf(engine).Features.ContainsKey(name), $"«{name}» é afinal uma base::Feature do libcef.dll ({engine})");
        Assert.DoesNotContain(name, FeatureNames());
    }

    // O próprio teste distingue: estes nomes não são base::Feature do motor (ver Dead).
    public static IEnumerable<object[]> DeadNames() => PerEngine(Dead.Select(n => new object[] { n }));

    [Theory]
    [MemberData(nameof(DeadNames))]
    public void DeadNames_AreNotFound(string engine, string name)
        => Assert.False(ScanOf(engine).Features.ContainsKey(name), $"«{name}» é afinal uma base::Feature do libcef.dll ({engine})");

    private static EngineScan Scan(string path)
    {
        var names = FeatureNames().Concat(Dead).Concat(Absent).Append("RootScrollbarFollowsBrowserTheme").Distinct(StringComparer.Ordinal).ToList();
        var image = File.ReadAllBytes(path);
        var span = image.AsSpan();
        // Flags do V8: o nome com um zero antes (ou no início do ficheiro) e outro depois.
        var v8Flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in V8FlagNames())
        {
            var bytes = Encoding.ASCII.GetBytes(flag + "\0");
            for (int from = 0, at; (at = span[from..].IndexOf(bytes)) >= 0; from += at + 1)
            {
                if (from + at == 0 || image[from + at - 1] == 0) { v8Flags.Add(flag); break; }
            }
        }
        // Cabeçalhos PE: base da imagem (PE32+) e secções (para passar de posição no ficheiro a endereço virtual).
        var pe = BitConverter.ToInt32(image, 0x3C);
        var sections = BitConverter.ToUInt16(image, pe + 6);
        var optional = pe + 24;
        var optionalSize = BitConverter.ToUInt16(image, pe + 20);
        Assert.Equal(0x20B, BitConverter.ToUInt16(image, optional));
        var imageBase = BitConverter.ToUInt64(image, optional + 24);
        var table = new List<(uint Va, uint Size, uint Raw, uint RawSize)>();
        for (var i = 0; i < sections; i++)
        {
            var s = optional + optionalSize + i * 40;
            table.Add((BitConverter.ToUInt32(image, s + 12), BitConverter.ToUInt32(image, s + 8),
                BitConverter.ToUInt32(image, s + 20), BitConverter.ToUInt32(image, s + 16)));
        }
        ulong? AddressOf(int offset)
        {
            foreach (var (va, _, raw, rawSize) in table)
                if (offset >= raw && offset < raw + rawSize) return imageBase + va + (ulong)(offset - raw);
            return null;
        }
        // Endereço de cada cadeia candidata («kNome\0» → o «N»; «\0Nome\0» → o «N»).
        var wanted = new Dictionary<ulong, string>();
        foreach (var name in names)
        {
            foreach (var pattern in new[] { "k" + name + "\0", name + "\0" })
            {
                var bytes = Encoding.ASCII.GetBytes(pattern);
                var skip = pattern[0] == 'k' ? 1 : 0;
                for (int from = 0, at; (at = span[from..].IndexOf(bytes)) >= 0; from += at + 1)
                {
                    var offset = from + at;
                    // Antes da cadeia não pode haver outra letra do nome (o fim de «kOutroNome»); outros bytes podem
                    // ser dados vizinhos sem o zero (no CEF 156: «kNetworkServiceSandbox» vem depois de 0xF2).
                    if (offset > 0 && (char.IsAsciiLetterOrDigit((char)image[offset - 1]) || image[offset - 1] == (byte)'_')) continue;
                    if (AddressOf(offset + skip) is { } address) wanted.TryAdd(address, name);
                }
            }
        }
        // Uma só passagem pelos ponteiros alinhados: o nome seguido do estado por omissão (0 ou 1).
        var found = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var i = 0; i + 12 <= image.Length; i += 8)
        {
            var value = BitConverter.ToUInt64(image, i);
            if (value < imageBase || !wanted.TryGetValue(value, out var name)) continue;
            var state = BitConverter.ToInt32(image, i + 8);
            if (state is not (0 or 1)) continue;
            if (!found.TryGetValue(name, out var states)) found[name] = states = [];
            states.Add(state);
        }
        return new EngineScan(found, v8Flags);
    }
}
