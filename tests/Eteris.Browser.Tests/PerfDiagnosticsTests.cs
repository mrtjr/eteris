using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// Modos de diagnóstico das medições de desempenho (plano 0.2.14, fase 0; build/perf/README.md): só com as variáveis
// ETERIS_TEST_*, e nunca numa instalação (uma variável esquecida no PC de alguém não muda o Éteris instalado).
public class PerfDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("tudo")]
    public void Pure_IsOffWithoutAValidValue(string? value)
        => Assert.Equal(PerfLayers.None, PerfDiagnostics.ParsePure(value, installed: false));

    [Fact]
    public void Pure_OneTurnsEveryLayerOff()
        => Assert.Equal(PerfLayers.All, PerfDiagnostics.ParsePure(" 1 ", installed: false));

    [Fact]
    public void Pure_ListTurnsOnlyThoseLayersOff()
    {
        Assert.Equal(PerfLayers.DevTools | PerfLayers.Media, PerfDiagnostics.ParsePure("devtools, midia", installed: false));
        Assert.Equal(PerfLayers.Media | PerfLayers.Animations, PerfDiagnostics.ParsePure("Mídia,animações", installed: false));
        Assert.Equal(PerfLayers.Overlay | PerfLayers.Mica, PerfDiagnostics.ParsePure("camada,mica,desconhecida", installed: false));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("devtools,midia,camada,mica,animacoes")]
    public void Pure_IsInertInAnInstall(string value)
        => Assert.Equal(PerfLayers.None, PerfDiagnostics.ParsePure(value, installed: true));

    [Fact]
    public void All_CoversEveryLayer()
    {
        var every = Enum.GetValues<PerfLayers>().Where(l => l is not PerfLayers.None and not PerfLayers.All)
            .Aggregate(PerfLayers.None, (a, b) => a | b);
        Assert.Equal(PerfLayers.All, every);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void Counters_AreOffWithoutTheVariable(string? value)
        => Assert.Null(PerfDiagnostics.ResolveCountersPath(value, 42, installed: false));

    [Fact]
    public void Counters_OneWritesToTempWithTheProcessId()
        => Assert.Equal(Path.Combine(Path.GetTempPath(), "eteris-contadores-42.tsv"), PerfDiagnostics.ResolveCountersPath("1", 42, installed: false));

    [Fact]
    public void Counters_PathKeepsItAndReplacesThePid()
        => Assert.Equal(@"C:\m\c-42.tsv", PerfDiagnostics.ResolveCountersPath(@" C:\m\c-{pid}.tsv ", 42, installed: false));

    [Fact]
    public void Counters_AreInertInAnInstall()
    {
        Assert.Null(PerfDiagnostics.ResolveCountersPath("1", 42, installed: true));
        Assert.Null(PerfDiagnostics.ResolveCountersPath(@"C:\m\c.tsv", 42, installed: true));
    }

    [Theory]
    [InlineData("MAP *.eteris-teste.example 127.0.0.1", "MAP *.eteris-teste.example 127.0.0.1")]
    [InlineData(" MAP  a.eteris-teste.example   127.0.0.1:8790 ", "MAP a.eteris-teste.example 127.0.0.1:8790")]
    [InlineData("MAP *.a.test 127.0.0.1, MAP *.b.invalid 127.0.0.1", "MAP *.a.test 127.0.0.1,MAP *.b.invalid 127.0.0.1")]
    [InlineData("map *.site.localhost 127.0.0.1", "map *.site.localhost 127.0.0.1")]
    public void HostRules_AcceptOnlyReservedNamesToLoopback(string value, string expected)
        => Assert.Equal(expected, PerfDiagnostics.ParseHostRules(value, installed: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // Um site real nunca pode ser desviado, nem para o próprio PC.
    [InlineData("MAP *.google.com 127.0.0.1")]
    [InlineData("MAP * 127.0.0.1")]
    // Só para o próprio PC.
    [InlineData("MAP *.eteris-teste.example 10.0.0.1")]
    [InlineData("MAP *.eteris-teste.example 127.0.0.2")]
    [InlineData("MAP *.eteris-teste.example eteris.example")]
    // Outras regras do Chromium (EXCLUDE, ~NOTFOUND) ficam de fora.
    [InlineData("EXCLUDE *.eteris-teste.example")]
    [InlineData("MAP *.eteris-teste.example ~NOTFOUND")]
    // Uma regra inválida desliga a lista inteira.
    [InlineData("MAP *.a.example 127.0.0.1, MAP *.b.com 127.0.0.1")]
    public void HostRules_RefuseEverythingElse(string? value)
        => Assert.Null(PerfDiagnostics.ParseHostRules(value, installed: false));

    [Fact]
    public void HostRules_AreInertInAnInstall()
        => Assert.Null(PerfDiagnostics.ParseHostRules("MAP *.eteris-teste.example 127.0.0.1", installed: true));

    // A instalação do Velopack: <raiz>\Update.exe e <raiz>\current\Eteris.Browser.exe.
    [Fact]
    public void DetectInstalled_LooksForTheVelopackUpdaterOneFolderUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "eteris-perf-tests-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        try
        {
            Assert.False(PerfDiagnostics.DetectInstalled(current));
            File.WriteAllText(Path.Combine(root, "Update.exe"), string.Empty);
            Assert.True(PerfDiagnostics.DetectInstalled(current));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Sem as variáveis (como nos testes), nada muda e os contadores não escrevem nada.
    [Fact]
    public void WithoutTheVariables_NothingChanges()
    {
        Assert.Equal(PerfLayers.None, PerfDiagnostics.PureLayers);
        Assert.False(PerfDiagnostics.Without(PerfLayers.DevTools));
        Assert.Null(PerfDiagnostics.HostRules);
        Assert.Null(PerfDiagnostics.CountersPath);
        Assert.False(PerfCounters.IsOn);
        PerfCounters.Add("x");
        PerfCounters.AddRequest(0, cancelled: true);
        PerfCounters.Flush();
    }

    [Theory]
    [InlineData("C:\\app\\Eteris.Browser.exe --type=renderer --lang=pt-PT", "renderer")]
    [InlineData("x --type=gpu-process --field-trial-handle=1", "gpu-process")]
    [InlineData("x --type=utility --utility-sub-type=network.mojom.NetworkService --lang=pt", "utility:NetworkService")]
    [InlineData("x --type=crashpad-handler", "crashpad-handler")]
    [InlineData("x --no-type", "sem-tipo")]
    [InlineData(null, "?")]
    [InlineData("", "?")]
    public void ChildType_ReadsTheChromiumProcessType(string? commandLine, string expected)
        => Assert.Equal(expected, StartupTrace.ChildType(commandLine));

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(Root, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "obj" or "bin" or "publish"));

    // As variáveis do diagnóstico só se leem em PerfDiagnostics, que as ignora numa instalação: ninguém as lê à parte.
    [Fact]
    public void DiagnosticVariables_AreReadOnlyThroughPerfDiagnostics()
    {
        string[] names = [PerfDiagnostics.PureVariable, PerfDiagnostics.CountersVariable, PerfDiagnostics.HostRulesVariable, PerfDiagnostics.PressureVariable,
            PerfDiagnostics.EnergyVariable];
        var readers = SourceFiles()
            .Where(f => names.Any(n => File.ReadAllText(f).Contains('"' + n + '"', StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f))
            .ToList();
        Assert.Equal(new[] { "PerfDiagnostics.cs" }, readers);
    }

    // O contador de fotogramas do WPF não pode ouvir o CompositionTarget.Rendering: um ouvinte nesse evento obriga o WPF a
    // desenhar todos os fotogramas, e a prova do E1 (0 lotes em repouso) ficava impossível.
    [Fact]
    public void WpfCounter_NeverListensToCompositionTargetRendering()
    {
        var code = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Infrastructure", "PerfCounters.cs"));
        var withoutComments = Regex.Replace(code, @"//.*$", string.Empty, RegexOptions.Multiline);
        Assert.DoesNotContain("CompositionTarget", withoutComments, StringComparison.Ordinal);
        Assert.Contains("CommittingBatch", withoutComments, StringComparison.Ordinal);
    }
}
