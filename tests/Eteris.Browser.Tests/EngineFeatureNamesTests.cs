using System.IO;
using System.Text;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Revisão aos 50: um nome em enable-features/disable-features que não é uma base::Feature do motor é ignorado em
// silêncio — e a promessa em eteris://privacy/ ficava verde por causa de um interruptor que não existe. Cada nome que o
// Éteris põe na linha de comandos tem de ter, no libcef.dll em uso, a estrutura base::Feature: um ponteiro para o
// nome (no CEF 156 a cadeia é «kNome» e o ponteiro aponta para o «N»; nas versões antigas, para «Nome») seguido do
// estado por omissão (0 ou 1).
public class EngineFeatureNamesTests
{
    // Cada nome encontrado como base::Feature, com o(s) estado(s) por omissão lido(s) a seguir ao ponteiro.
    private static readonly Lazy<Dictionary<string, HashSet<int>>> Scanned = new(Scan);
    private static readonly Lazy<HashSet<string>> Present = new(() => Scanned.Value.Keys.ToHashSet(StringComparer.Ordinal));

    public static IEnumerable<object[]> Names()
        => EngineHardening.AdApis.Concat(EngineHardening.Partitioning).Concat(EngineHardening.LocalNetworkChecks)
            .Concat(EngineHardening.Identity).Concat(EngineHardening.NoListeningSockets).Concat(EngineHardening.NoGoogleAtStartup)
            .Select(f => f.Feature).Append("HttpsUpgrades").Append(NetworkSandbox.Feature)
            // As de desempenho levam parâmetros («Nome:param/valor»): procura-se só o nome. As «V8Flag_x» não são
            // base::Feature (o gin passa-as ao V8 como --x): têm o seu teste, mais abaixo.
            .Concat(EnginePerformance.Enabled.Select(f => EnginePerformance.FeatureName(f.Feature))
                .Where(n => !n.StartsWith(EnginePerformance.V8FlagPrefix, StringComparison.Ordinal)))
            .Distinct().Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Names))]
    public void EverySwitchedFeature_IsABaseFeatureOfTheEngine(string name)
        => Assert.True(Present.Value.Contains(name), $"«{name}» não é uma base::Feature do libcef.dll: o Chromium ignorava-o");

    // Fase 2 do plano de desempenho: as funcionalidades confirmadas na árvore do Chromium 156 (cc/base/features.cc,
    // components/viz/common/features.cc, external_begin_frame_source_win.cc, gpu/config/gpu_finch_features.cc,
    // third_party/blink/common/features.cc, spare_render_process_host_manager_impl.cc, partition_alloc_features.cc) têm de
    // estar também no binário em uso, e com o estado por omissão que o plano conta (todas desligadas, salvo a cache do
    // Graphite, ligada no Windows: é por isso que o Éteris a desliga sem o e3).
    [Theory]
    [InlineData("ThrottleRepeatedNoDamageFrames", 0)]
    [InlineData("ExternalBeginFrameSourceWinUsesRunOrPostTask", 0)]
    [InlineData("SingleVideoFrameRateThrottling", 0)]
    [InlineData("GpuPersistentCache", 0)]
    [InlineData("GpuPersistentCacheMetadata", 0)]
    [InlineData("DelegatedCompositing", 0)]
    [InlineData("SkiaGraphite", 0)]
    [InlineData("SkiaGraphiteUsePersistentCache", 1)]
    [InlineData("WebGLDiscardBackBuffer", 0)]
    [InlineData("OffscreenCanvasPropagateVisibility", 0)]
    [InlineData("KillSpareRenderOnMemoryPressure", 0)]
    [InlineData("SpareRPHUseCriticalMemoryPressure", 0)]
    [InlineData("PartitionAllocSortActiveSlotSpans", 0)]
    public void PhaseTwoFeatures_HaveTheDefaultThePlanCountsOn(string name, int state)
    {
        Assert.Contains(EnginePerformance.Enabled, f => EnginePerformance.FeatureName(f.Feature) == name);
        Assert.True(Scanned.Value.TryGetValue(name, out var found), $"«{name}» não é uma base::Feature do libcef.dll");
        Assert.Contains(state, found);
    }

    // «V8Flag_x»: a flag «x» do V8 tem de existir no binário (o V8 guarda o nome de cada flag numa tabela de cadeias);
    // uma flag que não existe era ignorada (o V8 avisa, mas na consola de um processo sem consola).
    public static IEnumerable<object[]> V8Flags()
        => EnginePerformance.Enabled.Select(f => EnginePerformance.FeatureName(f.Feature))
            .Where(n => n.StartsWith(EnginePerformance.V8FlagPrefix, StringComparison.Ordinal))
            .Select(n => new object[] { n[EnginePerformance.V8FlagPrefix.Length..] });

    [Theory]
    [MemberData(nameof(V8Flags))]
    public void EveryV8Flag_IsAFlagOfTheEngine(string flag)
    {
        var image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "libcef.dll")).AsSpan();
        var bytes = Encoding.ASCII.GetBytes(flag + "\0");
        var found = false;
        for (int from = 0, at; !found && (at = image[from..].IndexOf(bytes)) >= 0; from += at + 1)
            found = from + at == 0 || image[from + at - 1] == 0;
        Assert.True(found, $"«{flag}» não é uma flag do V8 no libcef.dll: o V8 ignorava-a");
    }

    // O próprio teste distingue: estes nomes só existem como funcionalidades do Blink (ou numa tabela de nomes).
    [Theory]
    [InlineData("PartitionedCookies")]
    [InlineData("AttributionReporting")]
    [InlineData("SharedStorageAPI")]
    public void DeadNames_AreNotFound(string name) => Assert.DoesNotContain(name, Present.Value);

    private static Dictionary<string, HashSet<int>> Scan()
    {
        var names = Names().Select(n => (string)n[0]).Concat(["PartitionedCookies", "AttributionReporting", "SharedStorageAPI"]).ToList();
        var path = Path.Combine(AppContext.BaseDirectory, "libcef.dll");
        var image = File.ReadAllBytes(path);
        var span = image.AsSpan();
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
        return found;
    }
}
