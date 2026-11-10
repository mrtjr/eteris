using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// Ajustes de desempenho da 0.2.14 (fases 1 e 2 do plano): cada um com interruptor, desligáveis todos de uma vez sem
// compilar de novo, e os que mexem antes da 1.ª página desligados por omissão até a rede de segurança do sandbox os cobrir
// (R-2).
public class PerfTuningTests
{
    private static readonly string[] Exe = [@"C:\x\Eteris.Browser.exe"];

    [Fact]
    public void Ids_AreUniqueLowercaseExperimentReferences()
    {
        Assert.Equal(PerfTuning.Tweaks.Count, PerfTuning.Tweaks.Select(t => t.Id).Distinct().Count());
        Assert.All(PerfTuning.Tweaks, t => Assert.Matches(@"^e\d{1,2}(\.\d)?$", t.Id));
        Assert.All(PerfTuning.Tweaks, t => Assert.False(string.IsNullOrWhiteSpace(t.What)));
        // Uma experiência ou tem um ajuste só («e7») ou entradas numeradas («e14.1»…), nunca as duas coisas.
        Assert.All(PerfTuning.Tweaks.Where(t => t.Id.Contains('.')), t => Assert.DoesNotContain(PerfTuning.Tweaks, o => o.Id == t.Id.Split('.')[0]));
    }

    // Fase 2: as funcionalidades do motor (E14, E15) e a poupança de energia (E16) ficam todas desligadas até ao seu A/B,
    // marcadas como do motor e antes da 1.ª página; cada uma à espera do diagnóstico ou da decisão do plano.
    [Theory]
    [InlineData("e14.1", "D3/D5")]
    [InlineData("e14.2", "D3/D5")]
    [InlineData("e14.3", "D3/D5")]
    [InlineData("e14.4", "D3/D5")]
    [InlineData("e14.5", "D3/D5")]
    [InlineData("e14.6", "D-3")]
    [InlineData("e15.1", "D9")]
    [InlineData("e15.2", "D9")]
    [InlineData("e15.3", "D9")]
    [InlineData("e15.4", "D9")]
    [InlineData("e15.5", "D9")]
    [InlineData("e16", "D-8")]
    public void PhaseTwo_IsOffUntilMeasured(string id, string waits)
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == id);
        Assert.False(tweak.OnByDefault);
        Assert.True(tweak.BeforeFirstPaint);
        Assert.True(tweak.Engine);
        Assert.Equal(waits, tweak.Waits);
        Assert.DoesNotContain(id, PerfTuning.Resolve(Exe, testValue: null));
    }

    // O grupo «motor» são o E7 e a fase 2; o resto é da interface (fase 1).
    [Fact]
    public void EngineGroup_IsTheBackForwardCacheAndPhaseTwo()
        => Assert.Equal(
            ["e7", "e14.1", "e14.2", "e14.3", "e14.4", "e14.5", "e14.6", "e15.1", "e15.2", "e15.3", "e15.4", "e15.5", "e16"],
            PerfTuning.EngineIds);

    [Fact]
    public void ExperimentNumber_ExpandsToItsEntries()
    {
        var on = PerfTuning.Resolve([.. Exe, "--eteris-ajustes=e14,-e14.6"], null);
        Assert.Equal(["e14.1", "e14.2", "e14.3", "e14.4", "e14.5"], on.Where(id => id.StartsWith("e14", StringComparison.Ordinal)).Order());
        // «e1» é o ajuste e1, não o e13/e14… (a partir de «nada»: o e13 já vem ligado por omissão).
        Assert.Equal(["e1"], PerfTuning.Resolve(Exe, "nada,e1"));
        Assert.Equal(PerfTuning.Defaults.Append("e3").Order(), PerfTuning.Resolve([.. Exe, "--eteris-ajustes=e1,e3"], null).Order());
        Assert.Equal(PerfTuning.Defaults.Order(), PerfTuning.Resolve([.. Exe, "--eteris-ajustes=e9,e99"], null).Order());
    }

    [Fact]
    public void EngineGroup_TurnsTheEngineListOnAndOff()
    {
        var on = PerfTuning.Resolve(Exe, "motor");
        Assert.Superset(PerfTuning.EngineIds.ToHashSet(), on.ToHashSet());
        Assert.Contains("e1", on);
        var off = PerfTuning.Resolve([.. Exe, "--eteris-ajustes=-motor"], "tudo");
        Assert.DoesNotContain(off, id => PerfTuning.EngineIds.Contains(id));
        Assert.Contains("e3", off);
    }

    // O recuo geral das funcionalidades do motor: ganha ao argumento e à variável, e deixa os outros ajustes como estão.
    [Fact]
    public void EngineOffArgument_WinsOverTheListsAndKeepsTheRest()
    {
        var on = PerfTuning.Resolve([.. Exe, "--eteris-ajustes=tudo", " --ETERIS-SEM-AJUSTES-MOTOR "], "motor");
        Assert.DoesNotContain(on, id => PerfTuning.EngineIds.Contains(id));
        Assert.Contains("e3", on);
        Assert.Contains("e13", on);
        Assert.Empty(PerfTuning.Resolve([.. Exe, "--eteris-sem-ajustes-motor", "--eteris-sem-ajustes"], "tudo"));
    }

    [Fact]
    public void WithoutArguments_TheDefaultsHold()
        => Assert.Equal(PerfTuning.Defaults, PerfTuning.Resolve(Exe, testValue: null));

    // Os ligados por omissão na 0.2.14: os três de sempre e, desde a medição, o e8 (janela minimizada: 6,47% → 0,59% de
    // um núcleo) e o e13 (decisão D-6: sono também com pouca memória). Ligar mais um é decisão do dono, com medição.
    [Fact]
    public void Defaults_AreTheMeasuredOnes()
        => Assert.Equal(["e1", "e13", "e4", "e5", "e8"], PerfTuning.Defaults.Order(StringComparer.Ordinal));

    // Cada ligado por omissão desliga-se sozinho, pela variável de teste e pelo argumento (os braços A/B das medições
    // futuras comparam com e sem ele no mesmo binário).
    [Fact]
    public void EveryDefault_TurnsOffAlone()
        => Assert.All(PerfTuning.Defaults, id =>
        {
            var others = PerfTuning.Defaults.Where(o => o != id).Order().ToList();
            Assert.Equal(others, PerfTuning.Resolve(Exe, "-" + id).Order());
            Assert.Equal(others, PerfTuning.Resolve([.. Exe, "--eteris-ajustes=-" + id], null).Order());
        });

    // Regra R-2 do plano do sandbox: nada de código novo antes da 1.ª página sem a rede de segurança que o desligue
    // primeiro (D-18, ligada ao StartupGuard desde a fusão com o sandbox). Ligá-los por omissão é decisão do dono: até
    // lá só se ligam por interruptor.
    [Fact]
    public void BeforeFirstPaint_AreOffByDefault()
        => Assert.All(PerfTuning.Tweaks.Where(t => t.BeforeFirstPaint), t => Assert.False(t.OnByDefault, t.Id));

    // À espera de uma decisão do dono (D-4, D-5…) ou de um diagnóstico (D6, D9): desligado por omissão. O e8 e o e13 já
    // não esperam (D4 medido, D-6 decidida) e por isso já não têm Waits.
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

    // Cada ajuste é usado no código pelo nome da constante (nada de «e3» solto) e cada constante tem um ajuste. Os das
    // funcionalidades do motor são lidos pela lista (Shields/EnginePerformance.cs: EngineHardening.Apply passa-lhe o
    // PerfTuning.IsOn, EnginePerformanceTests): basta lá estarem.
    [Fact]
    public void EveryTweak_IsCheckedSomewhere()
    {
        var code = string.Join('\n', SourceFiles().Where(f => !f.EndsWith("PerfTuning.cs", StringComparison.Ordinal)).Select(File.ReadAllText));
        var constants = Regex.Matches(File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Infrastructure", "PerfTuning.cs")),
            @"public const string (\w+) = ""(e\d+(?:\.\d+)?)"";").Select(m => (Name: m.Groups[1].Value, Id: m.Groups[2].Value)).ToList();
        Assert.Equal(PerfTuning.Tweaks.Select(t => t.Id).Order(), constants.Select(c => c.Id).Order());
        var engineList = Shields.EnginePerformance.Enabled.Select(f => f.Tweak).ToHashSet();
        Assert.All(constants.Where(c => !engineList.Contains(c.Id)),
            c => Assert.Matches(@"PerfTuning\.IsOn\((Infrastructure\.)?PerfTuning\." + c.Name + @"\)", code));
        Assert.Contains("EnginePerformance.Apply(args, Infrastructure.PerfTuning.IsOn);", code);
        Assert.DoesNotMatch(@"PerfTuning\.IsOn\(""", code);
    }

    [Fact]
    public void Suspend_TurnsEverythingOffForThisProcess()
    {
        // O gancho só se testa pelo texto: chamá-lo aqui desligaria os ajustes nos outros testes deste processo.
        var code = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Infrastructure", "PerfTuning.cs"));
        Assert.Matches(@"IsOn\(string id\)\s*=>\s*_suspended is null\s*&&", code);
    }

    // A rede de segurança (D-18) só gasta o 1.º degrau quando a queda pode ser de um ajuste antes da 1.ª página: os ligados
    // por omissão correm todos depois dela (o e8 só deixa a vigia marcada no construtor da janela e não esconde uma
    // página que ainda não apresentou), por isso uma queda com a configuração de omissão continua a contar para o
    // sandbox.
    [Fact]
    public void BeforeFirstPaint_IsNoneWithTheDefaults()
    {
        Assert.False(PerfTuning.AnyBeforeFirstPaintIn(PerfTuning.Resolve(Exe, testValue: null)));
        Assert.True(PerfTuning.AnyBeforeFirstPaintIn(PerfTuning.Resolve([.. Exe, "--eteris-ajustes=e3"], null)));
        Assert.False(PerfTuning.AnyBeforeFirstPaintIn(PerfTuning.Resolve([.. Exe, "--eteris-ajustes=e13"], null)));
        Assert.False(PerfTuning.AnyBeforeFirstPaintIn(PerfTuning.Resolve([.. Exe, "--eteris-ajustes=tudo", "--eteris-sem-ajustes"], null)));
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
