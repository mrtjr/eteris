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
        // (desde a 0.2.12 o publish.ps1 publica com sandbox por omissão e pararia sem eles).
        Assert.Contains("publish.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox", publish);
        // E o pack.ps1 só empacota uma publicação sem sandbox quando isso é pedido.
        Assert.Contains("pack.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox", publish);
        Assert.DoesNotContain("publish.ps1", verify);
        Assert.Matches(@"sem_codecs:\n(\s+\w+:.*\n)*?\s+required: true\n(\s+\w+:.*\n)*?\s+type: boolean", yml);
    }

    private static string Job(string yml, string name)
        => Regex.Match(yml, "^  " + Regex.Escape(name) + @":\n(.*?)(?=^  \S|\z)", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
}
