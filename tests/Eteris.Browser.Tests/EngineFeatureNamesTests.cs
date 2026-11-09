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
    private static readonly Lazy<HashSet<string>> Present = new(Scan);

    public static IEnumerable<object[]> Names()
        => EngineHardening.AdApis.Concat(EngineHardening.Partitioning).Concat(EngineHardening.LocalNetworkChecks)
            .Concat(EngineHardening.Identity).Concat(EngineHardening.NoListeningSockets).Concat(EngineHardening.NoGoogleAtStartup)
            .Concat(EngineHardening.NoGoogleOnPages)
            .Select(f => f.Feature).Append("HttpsUpgrades").Append(NetworkSandbox.Feature).Distinct().Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Names))]
    public void EverySwitchedFeature_IsABaseFeatureOfTheEngine(string name)
        => Assert.True(Present.Value.Contains(name), $"«{name}» não é uma base::Feature do libcef.dll: o Chromium ignorava-o");

    // O próprio teste distingue: estes nomes só existem como funcionalidades do Blink (ou numa tabela de nomes).
    [Theory]
    [InlineData("PartitionedCookies")]
    [InlineData("AttributionReporting")]
    [InlineData("SharedStorageAPI")]
    public void DeadNames_AreNotFound(string name) => Assert.DoesNotContain(name, Present.Value);

    private static HashSet<string> Scan()
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
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 12 <= image.Length; i += 8)
        {
            var value = BitConverter.ToUInt64(image, i);
            if (value < imageBase || !wanted.TryGetValue(value, out var name)) continue;
            var state = BitConverter.ToInt32(image, i + 8);
            if (state is 0 or 1) found.Add(name);
        }
        return found;
    }
}
