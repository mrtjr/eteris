using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace Eteris.Browser.Tests;

// O ajudante que corre o JavaScript dos testes no Node: devolve a saída, falha com o erro do script, falha (em vez de
// passar em branco) quando a ferramenta não existe e mata o processo que não acaba no prazo.
public class NodeRunnerTests
{
    [Fact]
    public void Run_ReturnsTheOutput_WithUnixLineEndings()
        => Assert.Equal("a\nb", NodeRunner.Run("process.stdout.write('a\\r\\nb')"));

    [Fact]
    public void Run_FailsWithTheErrorText()
    {
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => NodeRunner.Run("throw new Error('falhou de proposito')"));
        Assert.Contains("falhou de proposito", error.Message);
    }

    [Fact]
    public void Start_AMissingTool_Fails()
    {
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => NodeRunner.Start("eteris-ferramenta-que-nao-existe.exe", [], TimeSpan.FromSeconds(5), "falta a ferramenta"));
        Assert.Contains("falta a ferramenta", error.Message);
    }

    [Fact]
    public void Start_AProcessThatHangs_IsKilledAtTheDeadline()
    {
        var clock = Stopwatch.StartNew();
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => NodeRunner.Start("node", ["-e", "setTimeout(() => {}, 60000)"], TimeSpan.FromSeconds(1), "o Node não está no PATH"));
        Assert.Contains("não acabou", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"demorou {clock.Elapsed.TotalSeconds:0.0} s");
    }

    // Um só sítio lança o Node: uma cópia nova do ajudante voltaria a poder passar em branco sem ele.
    [Fact]
    public void NoTestStartsNodeOutsideTheRunner()
    {
        var offenders = Directory.EnumerateFiles(TestsFolder(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) is not ("NodeRunner.cs" or "NodeRunnerTests.cs"))
            .Where(f => File.ReadAllText(f).Contains("ProcessStartInfo(\"node\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(offenders.Count == 0, "Lançam o Node fora do NodeRunner: " + string.Join(", ", offenders));
    }

    private static string TestsFolder([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
