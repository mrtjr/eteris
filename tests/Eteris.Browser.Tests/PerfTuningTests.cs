using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// Ajustes de desempenho da 0.2.14 (fase 1 do plano): cada um com interruptor, desligáveis todos de uma vez sem compilar
// de novo, e os que mexem antes da 1.ª página desligados por omissão até a rede de segurança do sandbox os cobrir (R-2).
public class PerfTuningTests
{
    private static readonly string[] Exe = [@"C:\x\Eteris.Browser.exe"];

    [Fact]
    public void Ids_AreUniqueLowercaseExperimentReferences()
    {
        Assert.Equal(PerfTuning.Tweaks.Count, PerfTuning.Tweaks.Select(t => t.Id).Distinct().Count());
        Assert.All(PerfTuning.Tweaks, t => Assert.Matches(@"^e\d{1,2}$", t.Id));
        Assert.All(PerfTuning.Tweaks, t => Assert.False(string.IsNullOrWhiteSpace(t.What)));
    }

    [Fact]
    public void WithoutArguments_TheDefaultsHold()
        => Assert.Equal(PerfTuning.Defaults, PerfTuning.Resolve(Exe, testValue: null));

    // Regra R-2 do plano do sandbox: nada de código novo antes da 1.ª página sem a rede de segurança que o desligue
    // primeiro (D-18). Até ela existir, estes ajustes só se ligam por interruptor.
    [Fact]
    public void BeforeFirstPaint_AreOffByDefault()
        => Assert.All(PerfTuning.Tweaks.Where(t => t.BeforeFirstPaint), t => Assert.False(t.OnByDefault, t.Id));

    // À espera de uma decisão do dono (D-4, D-5…) ou de um diagnóstico (D4): desligado por omissão.
    [Fact]
    public void Waiting_AreOffByDefault()
        => Assert.All(PerfTuning.Tweaks.Where(t => t.Waits is not null), t => Assert.False(t.OnByDefault, t.Id));

    [Fact]
    public void AllOff_WinsOverEverything()
    {
        Assert.Empty(PerfTuning.Resolve([.. Exe, "--eteris-ajustes=tudo", "--eteris-sem-ajustes"], "tudo"));
        Assert.Empty(PerfTuning.Resolve([.. Exe, " --ETERIS-SEM-AJUSTES "], null));
    }

    [Fact]
    public void Argument_TurnsOnAndOff()
    {
        var on = PerfTuning.Resolve([.. Exe, "--eteris-ajustes=e3,-e1,+E7, desconhecido"], null);
        Assert.Contains("e3", on);
        Assert.Contains("e7", on);
        Assert.DoesNotContain("e1", on);
        Assert.DoesNotContain("desconhecido", on);
    }

    [Fact]
    public void Argument_WinsOverTheTestVariable()
    {
        var on = PerfTuning.Resolve([.. Exe, "--eteris-ajustes=-e3"], "e3,e7");
        Assert.DoesNotContain("e3", on);
        Assert.Contains("e7", on);
    }

    [Fact]
    public void AllAndNothing_AreWords()
    {
        Assert.Equal(PerfTuning.Tweaks.Select(t => t.Id).ToHashSet(), PerfTuning.Resolve(Exe, "tudo"));
        Assert.Equal(new HashSet<string> { "e4" }, PerfTuning.Resolve(Exe, "nada,e4"));
    }

    // A variável de teste só se lê fora de uma instalação (como os outros ETERIS_TEST_*), e só o PerfTuning a lê.
    [Fact]
    public void TestVariable_IsReadOnlyByPerfTuningAndOnlyOutsideAnInstall()
    {
        var code = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Infrastructure", "PerfTuning.cs"));
        Assert.Matches(@"Read\(TestVariable\)\s*is\s*\{\s*\}\s*value\s*&&\s*!PerfDiagnostics\.IsInstalled", code);
        var readers = SourceFiles().Where(f => File.ReadAllText(f).Contains("ETERIS_TEST_AJUSTES", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)).ToList();
        Assert.Equal(["PerfTuning.cs"], readers);
    }

    // Cada ajuste é usado no código pelo nome da constante (nada de «e3» solto) e cada constante tem um ajuste.
    [Fact]
    public void EveryTweak_IsCheckedSomewhere()
    {
        var code = string.Join('\n', SourceFiles().Where(f => !f.EndsWith("PerfTuning.cs", StringComparison.Ordinal)).Select(File.ReadAllText));
        var constants = Regex.Matches(File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Infrastructure", "PerfTuning.cs")),
            @"public const string (\w+) = ""(e\d+)"";").Select(m => (Name: m.Groups[1].Value, Id: m.Groups[2].Value)).ToList();
        Assert.Equal(PerfTuning.Tweaks.Select(t => t.Id).Order(), constants.Select(c => c.Id).Order());
        Assert.All(constants, c => Assert.Matches(@"PerfTuning\.IsOn\((Infrastructure\.)?PerfTuning\." + c.Name + @"\)", code));
        Assert.DoesNotMatch(@"PerfTuning\.IsOn\(""", code);
    }

    [Fact]
    public void Suspend_TurnsEverythingOffForThisProcess()
    {
        // O gancho só se testa pelo texto: chamá-lo aqui desligaria os ajustes nos outros testes deste processo.
        var code = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Infrastructure", "PerfTuning.cs"));
        Assert.Matches(@"IsOn\(string id\)\s*=>\s*_suspended is null\s*&&", code);
    }

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
}
