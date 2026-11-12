using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// CI da v0.2.11: um teste com uma thread STA ficou mais de 20 s preso e o Assert.True do Join não dizia onde. Outro
// esperava pela thread sem prazo (um bloqueio penduraria a bateria até ao limite do job). As threads STA dos testes
// passam todas pelo StaRunner: de fundo, com prazo e com o passo na mensagem.
public class StaRunnerTests
{
    [Fact]
    public void StaThreads_HaveTimeouts()
    {
        // Uma thread STA nasce de Set/TrySetApartmentState; citar o valor do enum (por exemplo numa asserção sobre a
        // thread do StaRunner, como Run_ReturnsTheResultFromAnStaThread) não abre thread nenhuma e não conta.
        var setSta = new Regex(@"SetApartmentState\s*\(");
        var joinWithoutTimeout = new Regex(@"\.Join\(\s*\)");
        var offenders = new List<string>();
        foreach (var file in TestSources())
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (setSta.IsMatch(lines[i]) && name != "StaRunner.cs") offenders.Add($"{name}:{i + 1}: thread STA fora do StaRunner");
                if (joinWithoutTimeout.IsMatch(lines[i])) offenders.Add($"{name}:{i + 1}: Join sem prazo");
            }
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        var runner = File.ReadAllText(Path.Combine(TestsDir(), "StaRunner.cs"));
        Assert.Matches(setSta, runner); // a procura apanha a única thread STA permitida
        Assert.Contains("IsBackground = true", runner);
    }

    // Cada StaRunner.Run prende uma thread do pool enquanto espera pela thread STA: na fase paralela da bateria, com o
    // pool ocupado, era um teste intermitente (TFI-04). Os ficheiros que o usam correm na coleção sem paralelismo.
    [Fact]
    public void StaTests_RunInTheWpfCollection()
    {
        var missing = TestSources()
            .Where(f => Path.GetFileName(f) is not ("StaRunner.cs" or "StaRunnerTests.cs"))
            .Where(f => File.ReadAllText(f) is var text && text.Contains("StaRunner.Run", StringComparison.Ordinal)
                        && !text.Contains("[Collection(WpfCollection.Name)]", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, "Usam o StaRunner fora da coleção WpfCollection: " + string.Join(", ", missing));
    }

    [Fact]
    public void Run_ReturnsTheResultFromAnStaThread()
    {
        var state = StaRunner.Run(() => Thread.CurrentThread.GetApartmentState(), nameof(Run_ReturnsTheResultFromAnStaThread));
        Assert.Equal(ApartmentState.STA, state);
    }

    [Fact]
    public void Run_RethrowsTheErrorWithTheSteps()
    {
        var error = Assert.Throws<Xunit.Sdk.XunitException>(() => StaRunner.Run<int>(steps =>
        {
            steps.Mark("antes do erro");
            throw new InvalidOperationException("falhou de propósito");
        }, nameof(Run_RethrowsTheErrorWithTheSteps)));
        Assert.Contains("falhou de propósito", error.Message);
        Assert.Contains("antes do erro", error.Message);
    }

    [Fact]
    public void Run_WhenTheThreadHangs_SaysTheStepAndRunsOnTimeout()
    {
        var release = new ManualResetEventSlim();
        var cleaned = false;
        try
        {
            var error = Assert.Throws<Xunit.Sdk.XunitException>(() => StaRunner.Run<int>(steps =>
            {
                steps.Mark("à espera");
                release.Wait(TimeSpan.FromSeconds(30));
                return 0;
            }, nameof(Run_WhenTheThreadHangs_SaysTheStepAndRunsOnTimeout), onTimeout: () => cleaned = true, timeout: TimeSpan.FromMilliseconds(300)));
            Assert.Contains("presa no passo «à espera»", error.Message);
            Assert.True(cleaned);
        }
        finally
        {
            release.Set();
        }
    }

    private static IEnumerable<string> TestSources()
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(TestsDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    private static string TestsDir([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        => Path.GetDirectoryName(source)!;
}
