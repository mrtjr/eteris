using System.IO;
using System.Text.Json.Nodes;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// E16 do plano de desempenho da 0.2.14 (ajuste e16, à espera da decisão D-8): a poupança de energia do próprio Chromium,
// pela preferência global performance_tuning.battery_saver_mode.state no Local State, escrita na mesma passagem do DNS
// cifrado. Passo A por provar: se a CEF limita mesmo tudo a 30 fps com o estado 3.
public sealed class EnergySaverTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-energia-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Tweak_IsOffByDefaultAndWaitsForTheOwner()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.EnergySaver);
        Assert.False(tweak.OnByDefault);
        Assert.True(tweak.BeforeFirstPaint);
        Assert.True(tweak.Engine);
        Assert.Equal("D-8", tweak.Waits);
    }

    // Os valores do Chromium (prefs.h): 0 desligado, 1 com bateria baixa (o por omissão), 3 sempre; o 2 já não existe.
    [Fact]
    public void States_AreTheChromiumOnes()
    {
        Assert.Equal(["performance_tuning", "battery_saver_mode", "state"], EnergySaver.PrefPath);
        Assert.Equal(0, EnergySaver.Off);
        Assert.Equal(1, EnergySaver.LowBattery);
        Assert.Equal(3, EnergySaver.Always);
    }

    [Theory]
    [InlineData(false, null, null)]
    [InlineData(false, 3, null)]
    [InlineData(true, null, 1)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 3, 3)]
    public void State_NeedsTheTweakAndDefaultsToLowBattery(bool tweakOn, int? testState, int? expected)
        => Assert.Equal(expected, EnergySaver.StateFor(tweakOn, testState));

    // A variável de teste: só 0, 1 e 3, e nunca numa instalação.
    [Theory]
    [InlineData("0", false, 0)]
    [InlineData(" 3 ", false, 3)]
    [InlineData("1", false, 1)]
    [InlineData("2", false, null)]
    [InlineData("sim", false, null)]
    [InlineData(null, false, null)]
    [InlineData("3", true, null)]
    public void TestVariable_IsParsedAndInertWhenInstalled(string? value, bool installed, int? expected)
        => Assert.Equal(expected, PerfDiagnostics.ParseEnergy(value, installed));

    [Fact]
    public void WithoutTheVariable_NothingIsForced() => Assert.Null(PerfDiagnostics.EnergyState);

    // Por omissão (sem o ajuste) não se escreve nada.
    [Fact]
    public void ByDefault_NoStateIsWritten() => Assert.Null(EnergySaver.StateForStartup());

    [Fact]
    public void LocalState_WritesTheNestedPrefWithTheDns()
    {
        File.WriteAllText(_path, """{"ukm":{"x":1},"performance_tuning":{"high_efficiency_mode":{"state":2}}}""");
        Assert.True(LocalState.Apply(_path, "secure", "https://dns.quad9.net/dns-query", EnergySaver.Always));
        var json = JsonNode.Parse(File.ReadAllText(_path))!;
        Assert.Equal(3, (int)json["performance_tuning"]!["battery_saver_mode"]!["state"]!);
        Assert.Equal(2, (int)json["performance_tuning"]!["high_efficiency_mode"]!["state"]!);
        Assert.Equal(1, (int)json["ukm"]!["x"]!);
        Assert.Equal("secure", (string)json["dns_over_https"]!["mode"]!);
    }

    // Sem o ajuste, um estado escrito antes sai (vale o do Chromium); os vizinhos ficam.
    [Fact]
    public void LocalState_WithoutTheTweak_RemovesAnOldStateOnly()
    {
        File.WriteAllText(_path, """{"performance_tuning":{"battery_saver_mode":{"state":3},"high_efficiency_mode":{"state":2}}}""");
        Assert.True(LocalState.Apply(_path, "off", "", energySaver: null));
        var json = JsonNode.Parse(File.ReadAllText(_path))!;
        Assert.Null(json["performance_tuning"]!["battery_saver_mode"]);
        Assert.Equal(2, (int)json["performance_tuning"]!["high_efficiency_mode"]!["state"]!);

        File.WriteAllText(_path, """{"performance_tuning":{"battery_saver_mode":{"state":3}}}""");
        Assert.True(LocalState.ApplySecureDns(_path, "off", ""));
        Assert.Null(JsonNode.Parse(File.ReadAllText(_path))!["performance_tuning"]);
    }

    [Fact]
    public void LocalState_WithoutTheTweakAndWithoutAState_ChangesNothingElse()
    {
        File.WriteAllText(_path, """{"ukm":{"x":1}}""");
        Assert.True(LocalState.Apply(_path, "off", "", energySaver: null));
        var json = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.Equal(["ukm", "dns_over_https"], json.Select(p => p.Key));
    }

    // Um Local State de outra forma (performance_tuning que não é um objeto) não rebenta: o valor é substituído.
    [Fact]
    public void LocalState_OddShapes_AreHandled()
    {
        File.WriteAllText(_path, """{"performance_tuning":7}""");
        Assert.True(LocalState.Apply(_path, "off", "", EnergySaver.Off));
        Assert.Equal(0, (int)JsonNode.Parse(File.ReadAllText(_path))!["performance_tuning"]!["battery_saver_mode"]!["state"]!);
        File.WriteAllText(_path, """{"performance_tuning":7}""");
        Assert.True(LocalState.Apply(_path, "off", "", energySaver: null));
        Assert.Equal(7, (int)JsonNode.Parse(File.ReadAllText(_path))!["performance_tuning"]!);
    }

    [Fact]
    public void CorruptFile_IsLeftAlone()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.False(LocalState.Apply(_path, "secure", "x", EnergySaver.Always));
        Assert.Equal("{ not json", File.ReadAllText(_path));
    }

    // Vai na mesma escrita do Local State (nada a mais no disco antes da 1.ª página) e sem o ajuste nem se lê a variável.
    [Fact]
    public void Startup_WritesItInTheSameLocalStatePass()
    {
        var configurator = Source(Path.Combine("Infrastructure", "CefConfigurator.cs"));
        Assert.Contains("var energySaver = EnergySaver.StateForStartup();", configurator);
        Assert.Matches(@"LocalState\.Apply\([\s\S]*?energySaver\)", configurator);
        Assert.DoesNotContain("LocalState.ApplySecureDns(", configurator);
        var saver = Source(Path.Combine("Infrastructure", "EnergySaver.cs"));
        Assert.Matches(@"StateForStartup\(\)\s*=>\s*PerfTuning\.IsOn\(PerfTuning\.EnergySaver\)\s*\?\s*StateFor\(true,\s*PerfDiagnostics\.EnergyState\)\s*:\s*null;", saver);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
