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
        // O CI não tem o motor próprio nem o CefSharp com o patch do sandbox: a publicação de emergência é sem sandbox
        // (desde a 0.2.14 o publish.ps1 publica com sandbox por omissão e pararia sem eles).
        Assert.Contains("publish.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox", publish);
        // E o pack.ps1 só empacota uma publicação sem sandbox quando isso é pedido.
        Assert.Contains("pack.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox", publish);
        // Nem tem o google-oauth.json (nunca no repositório): o pack.ps1 recusa uma publicação sem login Google, salvo
        // com -SemLogin (SandboxPublishTests).
        Assert.Contains("pack.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox -SemLogin", publish);
        Assert.DoesNotContain("publish.ps1", verify);
        Assert.Matches(@"sem_codecs:\n(\s+\w+:.*\n)*?\s+required: true\n(\s+\w+:.*\n)*?\s+type: boolean", yml);
    }

    // CI instável (v0.2.11): um teste preso só se via como «Assert.True() Failure» e, sem prazo no job, um bloqueio
    // comia as 6 h por omissão. Os jobs que correm os testes têm prazo, o dotnet test deixa um dump se algum teste ficar
    // preso, grava o TRX (com a duração de cada teste) e envia sempre os resultados — também numa execução verde, para
    // se ver a duração de cada teste e procurar os que estão perto do prazo (TFI-06).
    [Theory]
    [InlineData("build.yml", "build", 30)]
    [InlineData("release.yml", "verify", 30)]
    [InlineData("release.yml", "publish-sem-codecs", 60)]
    public void TestJobs_HaveTimeouts_AndAlwaysKeepTheResults(string workflow, string job, int minutes)
    {
        var steps = Job(Workflow(workflow).Replace("\r\n", "\n"), job);
        Assert.Contains($"timeout-minutes: {minutes}", steps);
        var test = Regex.Match(steps, @"^\s+- run: dotnet test .*$", RegexOptions.Multiline).Value;
        Assert.Contains("--blame-hang-timeout 5m", test);
        Assert.Contains("--blame-hang-dump-type mini", test);
        Assert.Contains("--logger \"trx;LogFileName=testes.trx\"", test);
        Assert.Contains("--results-directory TestResults", test);
        var upload = steps[steps.IndexOf(test, StringComparison.Ordinal)..];
        Assert.Matches(@"- if: always\(\)\n\s+uses: actions/upload-artifact@\S+(?: #[^\n]*)?\n\s+with:\n\s+name: testes-.*\n\s+path: TestResults", upload);
    }

    // A bateria de repetição (manual e semanal): 20 corridas com dois processadores, para as intermitências que só
    // aparecem numa máquina apertada; sem repetir até passar — uma corrida vermelha deixa o job vermelho.
    [Fact]
    public void Stress_RunsTheSuiteTwentyTimes_WithTwoProcessors()
    {
        var yml = Workflow("stress.yml").Replace("\r\n", "\n");
        Assert.Contains("workflow_dispatch:", yml);
        Assert.Contains("schedule:", yml);
        Assert.Contains("permissions:\n  contents: read", yml);
        Assert.Contains("timeout-minutes: 120", yml);
        Assert.Contains("DOTNET_PROCESSOR_COUNT: 2", yml);
        Assert.Contains("$i -le 20", yml);
        Assert.Contains("--blame-hang-timeout 5m", yml);
        var upload = yml.IndexOf("path: TestResults", StringComparison.Ordinal);
        Assert.True(upload > 0, "o stress.yml não guarda TestResults");
        Assert.Contains("- if: always()", yml[..upload]);
    }

    // Os testes dos scripts injetados correm no Node e falham sem ele (NodeRunner): cada job que corre a bateria
    // instala-o, na versão da máquina de publicação, antes do dotnet test.
    [Theory]
    [InlineData("build.yml", "build")]
    [InlineData("release.yml", "verify")]
    [InlineData("release.yml", "publish-sem-codecs")]
    public void TestJobs_InstallTheNodeTheTestsNeed(string workflow, string job)
    {
        var steps = Job(Workflow(workflow).Replace("\r\n", "\n"), job);
        var node = steps.IndexOf("actions/setup-node@", StringComparison.Ordinal);
        var test = steps.IndexOf("dotnet test", StringComparison.Ordinal);
        Assert.True(node >= 0, $"{workflow} ({job}): sem actions/setup-node");
        Assert.True(test > node, $"{workflow} ({job}): o setup-node tem de vir antes do dotnet test");
        Assert.Matches(@"node-version: 22\.16\.0\n", steps[node..test]);
    }

    // A publicação de emergência compila o Rust, publica, empacota e envia: prazo maior, mas com prazo.
    [Fact]
    public void Release_PublishSemCodecs_HasATimeout()
        => Assert.Contains("timeout-minutes: 60", Job(Workflow("release.yml").Replace("\r\n", "\n"), "publish-sem-codecs"));

    // Os testes que arrancam o Éteris (tests/Eteris.Browser.E2E.Tests, fora da solução) têm o seu fluxo: compila o
    // executável em Release, corre-os com prazo, e os resultados guardam-se mesmo quando passam.
    [Fact]
    public void E2E_BuildsTheAppAndRunsTheLiveTests()
    {
        var yml = Workflow("e2e.yml").Replace("\r\n", "\n");
        Assert.Contains("permissions:\n  contents: read", yml);
        var e2e = Job(yml, "e2e");
        Assert.Contains("timeout-minutes: 30", e2e);
        Assert.Contains("dotnet build src/Eteris.Browser -c Release", e2e);
        Assert.Contains("dotnet test tests/Eteris.Browser.E2E.Tests -c Release", e2e);
        var upload = e2e.IndexOf("path: TestResults", StringComparison.Ordinal);
        Assert.True(upload > 0, "o job não guarda TestResults");
        Assert.Contains("- if: always()", e2e[..upload]);
    }

    private static string Job(string yml, string name)
        => Regex.Match(yml, "^  " + Regex.Escape(name) + @":\n(.*?)(?=^  \S|\z)", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
}
