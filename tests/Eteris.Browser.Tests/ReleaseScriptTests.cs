using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// CAD-02 e ARQ-01 (auditoria 2): a release sobe como rascunho e só o build/release.ps1 a publica, depois de conferir que
// a etiqueta é o HEAD (e a publish\ desse commit), que os fluxos do CI desse commit acabaram verdes (o build incluído) e
// que o releases.win.json do rascunho tem uma assinatura válida para as chaves de update-keys.txt. O script corre aqui
// numa cópia, com git, gh e dotnet falsos (.cmd) que só escrevem os seus argumentos num registo: nada vai à rede.
// Os textos procurados são só ASCII: a consola do CI (página 437) não é a do dono (achado CAD-04).
public class ReleaseScriptTests
{
    private static readonly string A = new('a', 40);
    private static readonly string B = new('b', 40);
    private const string Ok = """[{"workflowName":"build","status":"completed","conclusion":"success"},{"workflowName":"release","status":"completed","conclusion":"success"}]""";
    private const string Draft = """{"isDraft":true}""";

    private sealed record Scenario
    {
        public string Head { get; init; } = A;
        public string TagCommit { get; init; } = A;
        public string? PublishCommit { get; init; }
        public string RunsJson { get; init; } = Ok;
        public string ReleaseJson { get; init; } = Draft;
        public bool HasSignature { get; init; } = true;
        public int VerifyExit { get; init; }
        public bool LocalIndex { get; init; } = true;
        public string Switches { get; init; } = "";
    }

    private sealed record Run(int ExitCode, string[] Calls, Func<string, bool> Said, string Output)
    {
        public int Line(Func<string, bool> match) => Array.FindIndex(Calls, l => match(l));
        public int SignLine => Line(l => l.Contains("-- sign", StringComparison.Ordinal));
        public int VerifyLine => Line(l => l.Contains("-- verify", StringComparison.Ordinal));
        public int EditLine => Line(l => l.Contains("release edit", StringComparison.Ordinal));
    }

    private static void AssertRefused(Run run, string expected)
    {
        Assert.True(run.ExitCode != 0, "o release.ps1 não recusou; saída: " + run.Output);
        Assert.True(run.Said(expected), $"o release.ps1 não recusou com «{expected}»; saída: " + run.Output);
        Assert.True(run.EditLine < 0, "o release.ps1 publicou (release edit) apesar de recusar");
    }

    [Fact]
    public async Task Release_WithoutAGreenBuildForTheTagCommit_RefusesBeforePublishing()
        => AssertRefused(await RunReleaseAsync(new Scenario { RunsJson = "[]" }), "fluxo build para o commit");

    [Theory]
    [InlineData("""[{"workflowName":"build","status":"completed","conclusion":"success"},{"workflowName":"release","status":"completed","conclusion":"failure"}]""", "falhou")]
    [InlineData("""[{"workflowName":"build","status":"in_progress","conclusion":""}]""", "a correr")]
    public async Task Release_WithAFailedOrRunningWorkflow_Refuses(string runs, string expected)
        => AssertRefused(await RunReleaseAsync(new Scenario { RunsJson = runs }), expected);

    [Fact]
    public async Task Release_WhenTheTagIsNotHead_Refuses()
        => AssertRefused(await RunReleaseAsync(new Scenario { Head = A, TagCommit = B }), "e o HEAD");

    [Fact]
    public async Task Release_WhenThePublishIsFromAnotherCommit_Refuses()
        => AssertRefused(await RunReleaseAsync(new Scenario { PublishCommit = B }), "eteris-build.json");

    [Fact]
    public async Task Release_AlreadyPublished_Refuses()
        => AssertRefused(await RunReleaseAsync(new Scenario { ReleaseJson = """{"isDraft":false}""" }), "rascunho");

    [Fact]
    public async Task Release_WithoutASignature_Refuses()
        => AssertRefused(await RunReleaseAsync(new Scenario { HasSignature = false }), "releases.win.json.sig");

    [Fact]
    public async Task Release_WithAnInvalidSignature_Refuses()
        => AssertRefused(await RunReleaseAsync(new Scenario { VerifyExit = 1 }), "update-keys.txt");

    [Fact]
    public async Task Release_GreenAndSigned_PublishesTheDraft()
    {
        var run = await RunReleaseAsync(new Scenario { PublishCommit = A });
        Assert.True(run.ExitCode == 0, "o release.ps1 não publicou; saída: " + run.Output);
        var edit = run.Line(l => l.Contains("release edit v9.9.9", StringComparison.Ordinal));
        Assert.True(run.VerifyLine >= 0 && run.VerifyLine < edit, "a verificação vem antes de publicar");
        Assert.Contains("--draft=false", run.Calls[edit]);
        Assert.True(run.SignLine < 0, "sem -Key não se assina nada");
    }

    [Fact]
    public async Task Release_WithKey_SignsTheLocalIndex_UploadsTheSignature_ThenVerifiesWhatIsInTheRelease()
    {
        var run = await RunReleaseAsync(new Scenario { Switches = "-Key x.pem" });
        Assert.True(run.ExitCode == 0, "o release.ps1 não publicou; saída: " + run.Output);
        var sign = run.SignLine;
        var upload = run.Line(l => l.Contains("release upload v9.9.9", StringComparison.Ordinal));
        var download = run.Line(l => l.Contains("release download", StringComparison.Ordinal));
        Assert.True(sign >= 0, "assinou");
        Assert.Contains(@"Releases\releases.win.json", run.Calls[sign]);
        Assert.DoesNotContain("assinatura", run.Calls[sign]);
        Assert.True(upload > sign, "sobe a assinatura depois de assinar");
        Assert.Contains("releases.win.json.sig", run.Calls[upload]);
        Assert.True(download > upload, "descarrega o que está na release depois de subir");
        Assert.True(run.VerifyLine > download, "verifica o que descarregou");
        Assert.Contains("assinatura", run.Calls[run.VerifyLine]);
        Assert.True(run.EditLine > run.VerifyLine, "publica no fim");
    }

    [Fact]
    public async Task Release_WithKey_WithoutTheLocalIndex_Refuses()
    {
        var run = await RunReleaseAsync(new Scenario { Switches = "-Key x.pem", LocalIndex = false });
        AssertRefused(run, "pack.ps1");
        Assert.True(run.SignLine < 0, "não assinou nada");
    }

    [Fact]
    public async Task Release_Emergency_SignsTheIndexOfTheDraft()
    {
        var run = await RunReleaseAsync(new Scenario { Switches = "-Key x.pem -IndiceDaRelease", LocalIndex = false });
        Assert.True(run.ExitCode == 0, "o release.ps1 não publicou; saída: " + run.Output);
        var download = run.Line(l => l.Contains("release download", StringComparison.Ordinal));
        Assert.True(run.SignLine > download && download >= 0, "assina o índice depois de o descarregar do rascunho");
        Assert.Contains("assinatura", run.Calls[run.SignLine]);
        Assert.True(run.EditLine > run.VerifyLine && run.VerifyLine > run.SignLine, "verifica e só depois publica");
    }

    [Fact]
    public void Motor_HowToPublish_UploadsADraftAndPublishesWithTheScript()
    {
        var motor = File.ReadAllText(Path.Combine(Root(), "docs", "motor.md"));
        var start = motor.IndexOf("Por isso as releases normais publicam-se", StringComparison.Ordinal);
        var end = motor.IndexOf("A publicação pelo CI ficou só", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "o troço «Como publicar» de docs/motor.md mudou de forma");
        var section = motor[start..end];
        Assert.Contains(@"build\release.ps1", section);
        Assert.DoesNotContain("--publish", section);
    }

    // Copia o build\release.ps1 para uma pasta temporária com um repositório falso e corre-o com o PATH só com o sistema e
    // a pasta de ferramentas falsas (que só usam caminhos relativos a si próprias, %~dp0: o cmd lê o .cmd na página de
    // códigos da consola, e um caminho com acentos escrito lá dentro partia-se). Devolve a saída do script, as linhas do registo das ferramentas e uma função que diz
    // se o script escreveu um texto (sem espaços, como no SandboxPublishTests.RunPackAsync).
    private static async Task<Run> RunReleaseAsync(Scenario s)
    {
        var dir = Directory.CreateTempSubdirectory("eteris-release-");
        try
        {
            var root = dir.FullName;
            Directory.CreateDirectory(Path.Combine(root, "build"));
            var script = Path.Combine(root, "build", "release.ps1");
            File.Copy(Path.Combine(Root(), "build", "release.ps1"), script);
            Directory.CreateDirectory(Path.Combine(root, "Releases", "assinatura"));
            if (s.LocalIndex) File.WriteAllText(Path.Combine(root, "Releases", "releases.win.json"), "{\"Assets\":[]}");
            if (s.PublishCommit is not null)
            {
                Directory.CreateDirectory(Path.Combine(root, "publish"));
                File.WriteAllText(Path.Combine(root, "publish", "eteris-build.json"), "{ \"commit\": \"" + s.PublishCommit + "\" }");
            }
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            var log = Path.Combine(tools, "calls.log");
            File.WriteAllText(Path.Combine(tools, "runs.json"), s.RunsJson);
            File.WriteAllText(Path.Combine(tools, "release.json"), s.ReleaseJson);
            File.WriteAllText(Path.Combine(tools, "index.json"), "{\"Assets\":[]}");
            if (s.HasSignature) File.WriteAllText(Path.Combine(tools, "index.sig"), "AAAA");
            // Os .cmd escrevem o registo e leem as respostas pelo caminho deles próprios (%~dp0).
            File.WriteAllText(Path.Combine(tools, "git.cmd"), $"""
                @echo off
                echo git %* >> "%~dp0calls.log"
                if "%~3"=="rev-parse" (echo {s.Head}& exit /b 0)
                if "%~3"=="rev-list" (echo {s.TagCommit}& exit /b 0)
                exit /b 0

                """.Replace("\n", "\r\n"));
            File.WriteAllText(Path.Combine(tools, "gh.cmd"), $"""
                @echo off
                echo gh %* >> "%~dp0calls.log"
                if "%~1"=="run" if "%~2"=="list" (type "%~dp0runs.json"& exit /b 0)
                if "%~1"=="release" if "%~2"=="view" (type "%~dp0release.json"& exit /b 0)
                if "%~1"=="release" if "%~2"=="download" goto download
                exit /b 0
                :download
                copy /y "%~dp0index.json" "%~dp0..\Releases\assinatura\releases.win.json" >nul
                if exist "%~dp0index.sig" copy /y "%~dp0index.sig" "%~dp0..\Releases\assinatura\releases.win.json.sig" >nul
                exit /b 0

                """.Replace("\n", "\r\n"));
            File.WriteAllText(Path.Combine(tools, "dotnet.cmd"), $"""
                @echo off
                echo dotnet %* >> "%~dp0calls.log"
                :seek
                if "%~1"=="" exit /b 2
                if "%~1"=="--" goto found
                shift
                goto seek
                :found
                shift
                if "%~1"=="sign" goto sign
                if "%~1"=="verify" exit /b {s.VerifyExit}
                exit /b 0
                :sign
                type nul > "%~3.sig"
                exit /b 0

                """.Replace("\n", "\r\n"));

            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = root,
            };
            start.Environment["PATH"] = Environment.SystemDirectory + ";" + tools;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Version", "9.9.9" })
                start.ArgumentList.Add(argument);
            foreach (var argument in s.Switches.Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(argument);
            using var shell = Process.Start(start)!;
            var error = shell.StandardError.ReadToEndAsync();
            var output = await shell.StandardOutput.ReadToEndAsync();
            Assert.True(shell.WaitForExit(60_000), "powershell.exe não acabou em 60 s");
            var all = Regex.Replace(output + await error, @"\s+", "");
            var calls = File.Exists(log) ? File.ReadAllLines(log) : [];
            var masked = SandboxPublishTests.Masked(all, root) + " | registo: " + SandboxPublishTests.Masked(string.Join(" / ", calls), root);
            return new Run(shell.ExitCode, calls, text =>
            {
                Assert.True(text.All(char.IsAscii), $"o texto procurado «{text}» tem carateres fora do ASCII");
                return all.Contains(Regex.Replace(text, @"\s+", ""), StringComparison.Ordinal);
            }, masked);
        }
        finally { dir.Delete(recursive: true); }
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
