using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Guardas dos fluxos do GitHub Actions que só correm ao publicar (um erro aqui só aparecia no dia da versão).
public class WorkflowTests
{
    private static string Workflow(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", name));
    }

    // O MSBuild lê variáveis de ambiente como propriedades: VERSION=v0.2.0 virava Version=v0.2.0 no projeto de
    // testes, e o restauro falhava («'v0.2.0' não é uma versão válida») — as versões 0.1.1 e 0.2.0 não publicaram.
    [Theory]
    [InlineData("release.yml")]
    [InlineData("build.yml")]
    public void Workflows_DoNotSetEnvironmentVariablesThatMsBuildReadsAsProperties(string name)
    {
        var reserved = new[] { "VERSION", "VERSIONPREFIX", "VERSIONSUFFIX", "PACKAGEVERSION", "CONFIGURATION", "PLATFORM", "OUTPUTPATH" };
        foreach (Match env in Regex.Matches(Workflow(name), @"^\s+([A-Za-z_]+):", RegexOptions.Multiline))
            Assert.DoesNotContain(env.Groups[1].Value.ToUpperInvariant(), reserved);
    }

    // As releases publicam-se localmente com o motor com codecs (docs/motor.md, «Publicação»): o CI só tem o runtime
    // do NuGet, sem codecs. O push de uma tag só verifica; publicar pelo CI só por workflow_dispatch com sem_codecs.
    [Fact]
    public void Release_TagPushOnlyVerifies_AndCiPublishingNeedsSemCodecs()
    {
        var yml = Workflow("release.yml").Replace("\r\n", "\n");
        var jobs = Regex.Matches(yml[yml.IndexOf("\njobs:\n", StringComparison.Ordinal)..], @"^  ([A-Za-z0-9_-]+):\n", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["verify", "publish-sem-codecs"], jobs);

        var verify = Job(yml, "verify");
        Assert.Contains("if: github.event_name == 'push'", verify);
        Assert.Contains("UpdateService.cs", verify);
        Assert.Contains("dotnet test", verify);
        Assert.DoesNotContain("vpk", verify);
        Assert.DoesNotContain("pack.ps1", verify);
        Assert.DoesNotContain("contents: write", verify);

        var publish = Job(yml, "publish-sem-codecs");
        Assert.Contains("if: github.event_name == 'workflow_dispatch' && inputs.sem_codecs == true", publish);
        Assert.Contains("UpdateService.cs", publish);
        Assert.Contains("vpk upload", publish);
        Assert.Matches(@"sem_codecs:\n(\s+\w+:.*\n)*?\s+required: true\n(\s+\w+:.*\n)*?\s+type: boolean", yml);
    }

    // CI instável (v0.2.11): um teste preso só se via como «Assert.True() Failure» e, sem prazo no job, um bloqueio
    // comia as 6 h por omissão. Os jobs que correm os testes têm prazo, o dotnet test deixa um dump se algum teste ficar
    // preso, grava o TRX (com a duração de cada teste) e, quando falha, envia os resultados.
    [Theory]
    [InlineData("build.yml", "build")]
    [InlineData("release.yml", "verify")]
    public void TestJobs_HaveTimeouts_AndKeepTheResultsWhenTheyFail(string workflow, string job)
    {
        var steps = Job(Workflow(workflow).Replace("\r\n", "\n"), job);
        Assert.Contains("timeout-minutes: 30", steps);
        var test = Regex.Match(steps, @"^\s+- run: dotnet test .*$", RegexOptions.Multiline).Value;
        Assert.Contains("--blame-hang-timeout 5m", test);
        Assert.Contains("--blame-hang-dump-type mini", test);
        Assert.Contains("--logger \"trx;LogFileName=testes.trx\"", test);
        Assert.Contains("--results-directory TestResults", test);
        var upload = steps[steps.IndexOf(test, StringComparison.Ordinal)..];
        Assert.Matches(@"- if: failure\(\)\n\s+uses: actions/upload-artifact@v\d+\n\s+with:\n\s+name: testes-.*\n\s+path: TestResults", upload);
    }

    // A publicação de emergência compila o Rust, publica, empacota e envia: prazo maior, mas com prazo.
    [Fact]
    public void Release_PublishSemCodecs_HasATimeout()
        => Assert.Contains("timeout-minutes: 60", Job(Workflow("release.yml").Replace("\r\n", "\n"), "publish-sem-codecs"));

    private static string Job(string yml, string name)
        => Regex.Match(yml, "^  " + Regex.Escape(name) + @":\n(.*?)(?=^  \S|\z)", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
}
