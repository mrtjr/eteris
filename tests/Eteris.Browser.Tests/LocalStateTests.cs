using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public sealed class LocalStateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-ls-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void ApplySecureDns_MergesWithoutLosingOtherKeys()
    {
        File.WriteAllText(_path, """{"ukm":{"x":1},"dns_over_https":{"mode":"off"}}""");
        LocalState.ApplySecureDns(_path, "secure", "https://dns.quad9.net/dns-query");
        var json = JsonNode.Parse(File.ReadAllText(_path))!;
        Assert.Equal(1, (int)json["ukm"]!["x"]!);
        Assert.Equal("secure", (string)json["dns_over_https"]!["mode"]!);
        Assert.Equal("https://dns.quad9.net/dns-query", (string)json["dns_over_https"]!["templates"]!);
    }

    [Fact]
    public void ApplySecureDns_CreatesFileWhenMissing()
    {
        LocalState.ApplySecureDns(_path, "off", "");
        Assert.Equal("off", (string)JsonNode.Parse(File.ReadAllText(_path))!["dns_over_https"]!["mode"]!);
    }

    [Fact]
    public void CorruptFile_IsLeftAlone()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.False(LocalState.ApplySecureDns(_path, "secure", "x"));
        Assert.Equal("{ not json", File.ReadAllText(_path));
    }

    // INF-12: o Chromium grava o «Local State» com outra formatação e outro escape; comparam-se só as chaves geridas.
    [Fact]
    public void Unchanged_IsNotRewritten()
    {
        File.WriteAllText(_path, "{ \"outra\" : 1,\n  \"dns_over_https\" : { \"mode\" : \"secure\", \"templates\" : \"https://dns.example/dns-query\" } }");
        var before = File.ReadAllBytes(_path);
        var stamp = File.GetLastWriteTimeUtc(_path);
        Assert.True(LocalState.Apply(_path, "secure", "https://dns.example/dns-query", energySaver: null));
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void Changed_GoesThroughATemporaryFile()
    {
        File.WriteAllText(_path, "{\"dns_over_https\":{\"mode\":\"off\",\"templates\":\"\"}}");
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))        // outro processo com o ficheiro aberto
            Assert.False(LocalState.Apply(_path, "secure", "https://dns.example/dns-query", energySaver: null));
        Assert.Equal("{\"dns_over_https\":{\"mode\":\"off\",\"templates\":\"\"}}", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.True(LocalState.Apply(_path, "secure", "https://dns.example/dns-query", energySaver: null));
        Assert.Contains("\"secure\"", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void ReadOnlyFile_ReturnsFalse()
    {
        File.WriteAllText(_path, "{}");
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(LocalState.Apply(_path, "secure", "https://dns.example/dns-query", energySaver: null));
            Assert.False(File.Exists(_path + ".tmp"));
        }
        finally { File.SetAttributes(_path, FileAttributes.Normal); }
    }

    [Fact]
    public void EnergySaver_AlreadyAsAsked_IsNotRewritten()
    {
        Assert.True(LocalState.Apply(_path, "off", "", 3));
        File.SetLastWriteTimeUtc(_path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = File.ReadAllBytes(_path);
        var stamp = File.GetLastWriteTimeUtc(_path);
        Assert.True(LocalState.Apply(_path, "off", "", 3));
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_path));
        Assert.True(LocalState.Apply(_path, "off", "", energySaver: null));          // muda: a chave sai
        Assert.DoesNotContain(EnergySaver.PrefPath[1], File.ReadAllText(_path));     // "battery_saver_mode"
    }

    [Fact]
    public void Apply_WritesToATemporaryFileFlushesAndMoves()
    {
        var code = Sync.GoogleSyncTests.Src("Eteris.Browser", "Infrastructure", "LocalState.cs");
        Assert.Contains("path + \".tmp\"", code);
        Assert.Contains("Flush(true)", code);
        Assert.Contains("File.Move(temp, path, overwrite: true)", code);
        Assert.DoesNotContain("File.WriteAllText(path", code);
    }
}
