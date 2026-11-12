using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// CAD-02 e ARQ-01 (auditoria 2): a release sobe como rascunho e só o build/release.ps1 a publica, depois de conferir que
// a etiqueta é o HEAD (e a publish\ desse commit), que os fluxos do CI desse commit acabaram verdes (o build incluído) e
// que o releases.win.json do rascunho tem uma assinatura válida para as chaves de update-keys.txt. O script corre aqui
// numa cópia, com git, gh e dotnet falsos (.cmd) que escrevem os seus argumentos num registo: nada vai à rede.
// O «gh» falso guarda o que se lhe sobe e devolve-o no descarregamento seguinte, como o rascunho; o «dotnet» falso faz de
// build/sign: a «assinatura» de um ficheiro é a cópia dos bytes dele, e o verify só passa quando o índice descarregado é
// byte a byte o que se assinou (C1 da revisão final).
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
        // A resposta de «git ls-remote origin refs/tags/v9.9.9 refs/tags/v9.9.9^{}»: por omissão, uma etiqueta anotada
        // (o objeto da etiqueta e, na linha ^{}, o commit A).
        public string RemoteTags { get; init; } = Annotated(A);
        public string? PublishCommit { get; init; }
        // update-keys.txt no HEAD («git show HEAD:…»; nulo: não está no commit) e na árvore de trabalho. O verify falso só
        // aceita um ficheiro de chaves com CHAVE-VALIDA.
        public string? HeadKeys { get; init; } = "# chaves\nCHAVE-VALIDA\n";
        public string WorktreeKeys { get; init; } = "# chaves\nCHAVE-VALIDA\n";
        public string RunsJson { get; init; } = Ok;
        public string ReleaseJson { get; init; } = Draft;
        // Já há no rascunho um .sig válido para o índice dele (o caso sem -Key).
        public bool HasSignature { get; init; } = true;
        public int VerifyExit { get; init; }
        // Releases\ desta máquina: os pacotes, o assets.win.json e o releases.win.json do pack.ps1.
        public bool LocalIndex { get; init; } = true;
        // Um campo trocado no índice do rascunho (ver DraftIndex).
        public string? Change { get; init; }
        public string Switches { get; init; } = "";
    }

    // Os pacotes desta máquina: os da versão 9.9.9 (completo e delta) e o completo da anterior, que o releases.win.json
    // local também lista (o pack.ps1 junta todas as versões da pasta) mas o «vpk upload github» não sobe.
    private const string Full = "EterisBrowser-9.9.9-full.nupkg";
    private const string Delta = "EterisBrowser-9.9.9-delta.nupkg";
    private const string Older = "EterisBrowser-9.9.8-full.nupkg";
    private static readonly Dictionary<string, byte[]> Packages = new()
    {
        [Full] = Encoding.ASCII.GetBytes("pacote completo 9.9.9"),
        [Delta] = Encoding.ASCII.GetBytes("pacote delta 9.9.9 (mais curto)"),
        [Older] = Encoding.ASCII.GetBytes("pacote completo 9.9.8"),
    };

    private sealed record Entry(string PackageId, string Version, string Type, string FileName, string Sha256, string Size);

    private static Entry Of(string file, string version, string type)
        => new("EterisBrowser", version, type, file, Convert.ToHexString(SHA256.HashData(Packages[file])), Packages[file].Length.ToString());

    // Como o Velopack os escreve (Newtonsoft, compacto, enum em texto, SHA em maiúsculas): ReleaseEntryHelper.GetAssetFeedJson.
    private static string Feed(IEnumerable<Entry> entries)
        => "{\"Assets\":[" + string.Join(",", entries.Select(e => "{\"PackageId\":\"" + e.PackageId + "\",\"Version\":\"" + e.Version
            + "\",\"Type\":\"" + e.Type + "\",\"FileName\":\"" + e.FileName + "\",\"SHA1\":\"" + new string('0', 40) + "\",\"SHA256\":\""
            + e.Sha256 + "\",\"Size\":" + e.Size + ",\"NotesMarkdown\":\"\",\"NotesHTML\":\"\"}")) + "]}";

    // O índice local do pack.ps1: todas as versões da pasta, da mais recente para a mais antiga.
    private static string LocalFeed() => Feed([Of(Full, "9.9.9", "Full"), Of(Delta, "9.9.9", "Delta"), Of(Older, "9.9.8", "Full")]);

    // O índice que o «vpk upload github» põe no rascunho: só os Full e Delta do assets.win.json, noutra ordem; com um campo
    // trocado quando o cenário o pede.
    private static string DraftIndex(string? change)
    {
        var delta = Of(Delta, "9.9.9", "Delta");
        var full = Of(Full, "9.9.9", "Full");
        List<Entry> entries = change switch
        {
            "SHA256" => [delta, full with { Sha256 = Convert.ToHexString(SHA256.HashData(Packages[Older])) }],
            "Size" => [delta, full with { Size = (Packages[Full].Length + 1).ToString() }],
            "Version" => [delta, full with { Version = "9.9.8" }],
            "PackageId" => [delta, full with { PackageId = "OutroPacote" }],
            "FileName" => [delta with { FileName = "EterisBrowser-9.9.9-DELTA.nupkg" }, full],
            "Type" => [delta with { Type = "Full" }, full],
            "Extra" => [delta, full, Of(Older, "9.9.8", "Full")],
            "Missing" => [full],
            _ => [delta, full],
        };
        return Feed(entries);
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

    // As execuções «dinâmicas» do GitHub (event dynamic: as «Dependabot Updates», que correm sozinhas nos commits do main)
    // não são o CI deste código: uma falhada ou cancelada não pode bloquear a release, sem saída a não ser um commit novo.
    // O CI do próprio commit (build, release, e2e) continua a contar.
    [Fact]
    public async Task Release_IgnoresDynamicRuns_LikeDependabotUpdates()
    {
        const string runs = """[{"workflowName":"build","event":"push","status":"completed","conclusion":"success"},{"workflowName":"Dependabot Updates","event":"dynamic","status":"completed","conclusion":"failure"},{"workflowName":"Dependabot Updates","event":"dynamic","status":"in_progress","conclusion":""}]""";
        var run = await RunReleaseAsync(new Scenario { RunsJson = runs });
        Assert.True(run.ExitCode == 0, "o release.ps1 não publicou por causa de uma execução do Dependabot; saída: " + run.Output);
        Assert.True(run.EditLine >= 0, "o release.ps1 não publicou");
    }

    [Fact]
    public async Task Release_StillRefusesAFailedPushRun_EvenNextToDynamicOnes()
        => AssertRefused(await RunReleaseAsync(new Scenario { RunsJson = """[{"workflowName":"build","event":"push","status":"completed","conclusion":"success"},{"workflowName":"e2e","event":"push","status":"completed","conclusion":"failure"},{"workflowName":"Dependabot Updates","event":"dynamic","status":"completed","conclusion":"success"}]""" }), "falhou");

    [Fact]
    public async Task Release_WhenTheTagIsNotHead_Refuses()
        => AssertRefused(await RunReleaseAsync(new Scenario { Head = A, TagCommit = B }), "e o HEAD");

    private static string Annotated(string commit) => new string('c', 40) + "\trefs/tags/v9.9.9\n" + commit + "\trefs/tags/v9.9.9^{}\n";

    // M3 (revisão final): a etiqueta que conta é a de origin (a que o GitHub e o CI veem), não só a local.
    [Theory]
    [InlineData("", "em falta no remoto")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\trefs/tags/v9.9.9\n", "em origin aponta para")]
    [InlineData("cccccccccccccccccccccccccccccccccccccccc\trefs/tags/v9.9.9\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\trefs/tags/v9.9.9^{}\n", "em origin aponta para")]
    public async Task Release_WhenTheTagInOriginIsMissingOrElsewhere_Refuses(string remote, string expected)
        => AssertRefused(await RunReleaseAsync(new Scenario { RemoteTags = remote }), expected);

    // Uma etiqueta leve em origin (sem linha ^{}) vale pelo commit para que aponta.
    [Fact]
    public async Task Release_WithALightweightTagInOrigin_Publishes()
    {
        var run = await RunReleaseAsync(new Scenario { RemoteTags = A + "\trefs/tags/v9.9.9\n" });
        Assert.True(run.ExitCode == 0, "o release.ps1 não publicou; saída: " + run.Output);
        Assert.True(run.Line(l => l.Contains("ls-remote origin refs/tags/v9.9.9", StringComparison.Ordinal)) >= 0, "conferiu a etiqueta em origin");
    }

    // M4 (revisão final): a verificação usa as chaves do commit que se publica (as que o Éteris dessa versão leva), não as
    // da árvore de trabalho: uma chave por comitar publicava uma release que nenhum cliente verifica.
    [Fact]
    public async Task Release_WithAKeyOnlyInTheWorkingTree_Refuses()
    {
        var run = await RunReleaseAsync(new Scenario { HeadKeys = "# sem chaves\n" });
        AssertRefused(run, "update-keys.txt");
        Assert.True(run.VerifyLine >= 0, "não chegou a verificar");
        Assert.DoesNotContain(@"src\Eteris.Browser\Updates", run.Calls[run.VerifyLine]);
    }

    [Fact]
    public async Task Release_WithoutUpdateKeysInTheCommit_Refuses()
        => AssertRefused(await RunReleaseAsync(new Scenario { HeadKeys = null }), "git show");

    [Fact]
    public async Task Release_VerifiesWithTheKeysOfTheCommit()
    {
        var run = await RunReleaseAsync(new Scenario { WorktreeKeys = "# chave por comitar trocada\nOUTRA\n" });
        Assert.True(run.ExitCode == 0, "o release.ps1 não publicou; saída: " + run.Output);
        Assert.True(run.Line(l => l.Contains("show HEAD:src/Eteris.Browser/Updates/update-keys.txt", StringComparison.Ordinal)) >= 0, "leu as chaves do HEAD");
        Assert.Contains(@"Releases\assinatura", run.Calls[run.VerifyLine]);
    }

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

    // C1 (revisão final): o «vpk upload github» não sobe o Releases\releases.win.json desta máquina — gera outro, só com os
    // pacotes desta versão e noutra ordem. O release.ps1 confere o índice do rascunho com os pacotes daqui e assina os
    // bytes dele tal como vieram; depois de subir o .sig volta a descarregar os dois e verifica antes de publicar.
    [Fact]
    public async Task Release_WithKey_SignsTheDraftIndexAsDownloaded_AfterCheckingItAgainstTheLocalPackages()
    {
        var run = await RunReleaseAsync(new Scenario { Switches = "-Key x.pem", HasSignature = false });
        Assert.True(run.ExitCode == 0, "o release.ps1 não publicou; saída: " + run.Output);
        var sign = run.SignLine;
        var download = run.Line(l => l.Contains("release download", StringComparison.Ordinal));
        var upload = run.Line(l => l.Contains("release upload v9.9.9", StringComparison.Ordinal));
        var again = Array.FindLastIndex(run.Calls, l => l.Contains("release download", StringComparison.Ordinal));
        Assert.True(download >= 0 && sign > download, "assina o índice depois de o descarregar do rascunho");
        Assert.Contains(@"Releases\assinatura\releases.win.json", run.Calls[sign]);
        Assert.True(upload > sign, "sobe a assinatura depois de assinar");
        Assert.Contains("releases.win.json.sig", run.Calls[upload]);
        Assert.True(again > upload, "descarrega de novo o que está no rascunho depois de subir");
        Assert.True(run.VerifyLine > again, "verifica o que descarregou");
        Assert.Contains("assinatura", run.Calls[run.VerifyLine]);
        Assert.True(run.EditLine > run.VerifyLine, "publica no fim");
    }

    // Cada campo em que o Éteris confia (SignedUpdateSource), e o conjunto exato dos Full e Delta do assets.win.json: um
    // índice do rascunho que não bate com os pacotes daqui não se assina nem se publica.
    [Theory]
    [InlineData("SHA256", "campo SHA256")]
    [InlineData("Size", "campo Size")]
    [InlineData("Version", "campo Version")]
    [InlineData("PackageId", "campo PackageId")]
    [InlineData("FileName", "entrada a mais")]
    [InlineData("Type", "campo Type")]
    [InlineData("Extra", "entrada a mais")]
    [InlineData("Missing", "entrada em falta")]
    public async Task Release_WithKey_ADraftIndexThatDoesNotMatchTheLocalPackages_IsNotSigned(string change, string expected)
    {
        var run = await RunReleaseAsync(new Scenario { Switches = "-Key x.pem", HasSignature = false, Change = change });
        AssertRefused(run, expected);
        Assert.True(run.SignLine < 0, "assinou um índice que não bate com os pacotes locais");
        Assert.True(run.Line(l => l.Contains("release upload", StringComparison.Ordinal)) < 0, "subiu uma assinatura");
    }

    [Fact]
    public async Task Release_WithKey_WithoutTheLocalIndex_Refuses()
    {
        var run = await RunReleaseAsync(new Scenario { Switches = "-Key x.pem", LocalIndex = false, HasSignature = false });
        AssertRefused(run, "pack.ps1");
        Assert.True(run.SignLine < 0, "não assinou nada");
    }

    [Fact]
    public async Task Release_Emergency_SignsTheIndexOfTheDraft()
    {
        var run = await RunReleaseAsync(new Scenario { Switches = "-Key x.pem -IndiceDaRelease", LocalIndex = false, HasSignature = false });
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
            var releases = Directory.CreateDirectory(Path.Combine(root, "Releases")).FullName;
            if (s.LocalIndex)
            {
                foreach (var (file, bytes) in Packages) File.WriteAllBytes(Path.Combine(releases, file), bytes);
                File.WriteAllText(Path.Combine(releases, "releases.win.json"), LocalFeed());
                // Como o vpk pack o escreve (BuildAssets.Write): todos os ficheiros desta versão, os Setup e Portable também.
                File.WriteAllText(Path.Combine(releases, "assets.win.json"), "[{\"RelativeFileName\":\"EterisBrowser-win-Setup.exe\",\"Type\":\"Installer\"},"
                    + "{\"RelativeFileName\":\"" + Full + "\",\"Type\":\"Full\"},{\"RelativeFileName\":\"" + Delta + "\",\"Type\":\"Delta\"},"
                    + "{\"RelativeFileName\":\"EterisBrowser-win-Portable.zip\",\"Type\":\"Portable\"}]");
            }
            if (s.PublishCommit is not null)
            {
                Directory.CreateDirectory(Path.Combine(root, "publish"));
                File.WriteAllText(Path.Combine(root, "publish", "eteris-build.json"), "{ \"commit\": \"" + s.PublishCommit + "\" }");
            }
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            var log = Path.Combine(tools, "calls.log");
            File.WriteAllText(Path.Combine(tools, "runs.json"), s.RunsJson);
            File.WriteAllText(Path.Combine(tools, "remote.txt"), s.RemoteTags);
            if (s.HeadKeys is not null) File.WriteAllText(Path.Combine(tools, "head-keys.txt"), s.HeadKeys);
            var updates = Directory.CreateDirectory(Path.Combine(root, "src", "Eteris.Browser", "Updates")).FullName;
            File.WriteAllText(Path.Combine(updates, "update-keys.txt"), s.WorktreeKeys);
            File.WriteAllText(Path.Combine(tools, "release.json"), s.ReleaseJson);
            // O rascunho: o índice gerado pelo vpk upload e, se o cenário o disser, um .sig válido para ele.
            var draftIndex = DraftIndex(s.Change);
            File.WriteAllText(Path.Combine(tools, "index.json"), draftIndex);
            if (s.HasSignature) File.WriteAllText(Path.Combine(tools, "index.sig"), draftIndex);
            // Os .cmd escrevem o registo e leem as respostas pelo caminho deles próprios (%~dp0).
            File.WriteAllText(Path.Combine(tools, "git.cmd"), $"""
                @echo off
                echo git %* >> "%~dp0calls.log"
                if "%~3"=="rev-parse" (echo {s.Head}& exit /b 0)
                if "%~3"=="rev-list" (echo {s.TagCommit}& exit /b 0)
                if "%~3"=="ls-remote" (type "%~dp0remote.txt"& exit /b 0)
                if "%~3"=="show" goto show
                exit /b 0
                :show
                if not exist "%~dp0head-keys.txt" exit /b 128
                type "%~dp0head-keys.txt"
                exit /b 0

                """.Replace("\n", "\r\n"));
            File.WriteAllText(Path.Combine(tools, "gh.cmd"), $"""
                @echo off
                echo gh %* >> "%~dp0calls.log"
                if "%~1"=="run" if "%~2"=="list" (type "%~dp0runs.json"& exit /b 0)
                if "%~1"=="release" if "%~2"=="view" (type "%~dp0release.json"& exit /b 0)
                if "%~1"=="release" if "%~2"=="download" goto download
                if "%~1"=="release" if "%~2"=="upload" goto upload
                exit /b 0
                :download
                copy /y "%~dp0index.json" "%~dp0..\Releases\assinatura\releases.win.json" >nul
                if exist "%~dp0index.sig" copy /y "%~dp0index.sig" "%~dp0..\Releases\assinatura\releases.win.json.sig" >nul
                exit /b 0
                :upload
                copy /y "%~4" "%~dp0index.sig" >nul
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
                if "%~1"=="verify" goto verify
                exit /b 0
                :sign
                copy /y "%~3" "%~3.sig" >nul
                exit /b 0
                :verify
                findstr /c:"CHAVE-VALIDA" "%~2" >nul || exit /b 1
                fc /b "%~3" "%~4" >nul || exit /b 1
                exit /b {s.VerifyExit}

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

    // No Windows PowerShell 5.1 o Get-FileHash é uma função do módulo Microsoft.PowerShell.Utility. Lançado a partir do
    // PowerShell 7 (o runner do GitHub, ou um terminal pwsh), o powershell.exe herda o PSModulePath do 7 e o módulo não
    // carrega: «Get-FileHash is not recognized». O release.ps1 calcula o SHA256 com o .NET.
    [Fact]
    public void Release_HashesWithDotNet_NotWithAModuleFunction()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "build", "release.ps1"));
        Assert.DoesNotContain("Get-FileHash", script);
        Assert.Contains("[System.Security.Cryptography.SHA256]::Create()", script);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
