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
}
