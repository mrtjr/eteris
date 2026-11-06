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
}
