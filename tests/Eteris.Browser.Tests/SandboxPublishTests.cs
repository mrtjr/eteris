using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Eteris.Browser.Tests;

// Desde a 0.2.14 a publicação sai com o sandbox ligado por omissão (docs/sandbox.md, «Como publicar com o sandbox»):
// build/publish.ps1 usa o CefSharp com o patch v2 e a DLL cliente que build/sandbox/build-all.ps1 compila a partir do
// repositório; -NoSandbox fica só para emergências (a publicação pelo CI, que não tem nada disso).
public class SandboxPublishTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    // O param(...) do início do publish.ps1, numa linha ou em várias (a 0.2.13 acrescenta o -GoogleOAuthJson).
    public static string PublishParam(string publish)
    {
        var match = Regex.Match(publish, @"^param\(([^)]*)\)", RegexOptions.Multiline);
        Assert.True(match.Success, "publish.ps1 sem param(...)");
        return match.Groups[1].Value;
    }

    // Sem argumentos, o publish.ps1 publica com o sandbox: só há o -NoSandbox para sair dele, e não ligado por omissão.
    public static bool DefaultIsSandbox(string param)
        => Regex.IsMatch(param, @"\[switch\]\$NoSandbox(?!\s*=\s*\$true)", RegexOptions.IgnoreCase)
           && !Regex.IsMatch(param, @"\[switch\]\$Sandbox\b", RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("[string]$Version = \"0.2.14\", [switch]$NoSandbox, [string]$CefSharpFeed = \"\"", true)]
    [InlineData("[string]$Version = \"0.2.14\", [switch]$NoSandbox = $true", false)]
    [InlineData("[string]$Version = \"0.2.12\", [switch]$Sandbox, [string]$CefSharpFeed = \"\"", false)]
    [InlineData("[string]$Version = \"0.2.12\"", false)]
    public void DefaultIsSandbox_ReadsTheParamOfPublish(string param, bool expected)
        => Assert.Equal(expected, DefaultIsSandbox(param));

    [Fact]
    public void PublishParam_ReadsAParamOverSeveralLines()
    {
        var param = PublishParam("# cabeçalho\r\nparam([string]$Version = \"0.2.14\", [switch]$NoSandbox, [string]$CefSharpFeed = \"\",\r\n"
                                 + "  [string]$SandboxClientDll = \"\", [string]$GoogleOAuthJson = \"\")\r\n$ErrorActionPreference = \"Stop\"\r\n");
        Assert.Contains("[switch]$NoSandbox", param);
        Assert.Contains("$GoogleOAuthJson", param);
        Assert.DoesNotContain("ErrorActionPreference", param);
    }

    [Fact]
    public void Publish_IsWithSandboxByDefault_AndNoSandboxIsTheEmergencyExit()
    {
        var publish = Read("build", "publish.ps1");
        var param = PublishParam(publish);
        Assert.True(DefaultIsSandbox(param), param);
        Assert.Contains("[switch]$NoSandbox", param);
        Assert.DoesNotContain("[switch]$Sandbox", param);
        // O sandbox é o ramo «else» do -NoSandbox: sem argumentos, publica com ele.
        var branch = Regex.Match(publish, @"if \(\$NoSandbox\) \{(?<off>.*?)\} else \{(?<on>.*?)\r?\n\}", RegexOptions.Singleline);
        Assert.True(branch.Success, "publish.ps1 sem o if ($NoSandbox) { … } else { … }");
        Assert.DoesNotContain("EterisSandbox=true", branch.Groups["off"].Value);
        Assert.Contains("\"-p:EterisSandbox=true\"", branch.Groups["on"].Value);
        // Os sítios por omissão são os que o build-all.ps1 produz.
        Assert.Contains(@"build\cefsharp\feed", publish);
        Assert.Contains("eteris-cefsharp.json", publish);
        Assert.Contains(@"build\sandbox\client\out\Release\Eteris.Browser.dll", publish);
        Assert.Contains("build-all.ps1", publish);
        // E confirma a forma publicada (bootstrap + DLL cliente + Eteris.App, CefSharp com WindowsSandboxInfo).
        Assert.Contains("WindowsSandboxInfo", publish);
        Assert.Contains("Eteris.App.dll", publish);
    }

    // A publicação com sandbox só sai com os testes no modo com sandbox aprovados para o commit e as peças que publica
    // (o CI só corre o modo sem sandbox), e confere os binários do CefSharp publicados com o SHA256 do feed (a cache do
    // NuGet ganha sempre ao feed).
    [Fact]
    public void Publish_WithSandbox_NeedsTheSandboxTestsOfThisCommit()
    {
        var publish = Read("build", "publish.ps1");
        var on = Regex.Match(publish, @"if \(\$NoSandbox\) \{(?<off>.*?)\} else \{(?<on>.*?)\r?\n\}", RegexOptions.Singleline).Groups["on"].Value;
        Assert.Contains(@"build\sandbox\out\tests-ok.json", on);
        foreach (var check in new[] { "$testsInfo.commit -ne $commit", "if ($dirty)", "$testsInfo.feed_manifest_sha256", "$testsInfo.client_sha256",
                                      "$testsInfo.runtime_manifest_sha256" })
            Assert.Contains(check, on);
        Assert.Contains("\"runtimes/win-x64/lib/net6.0/$name\"", publish);
        Assert.Contains("a DLL cliente publicada não é a testada", publish);
        // As verificações dos testes vêm antes da compilação.
        Assert.True(publish.IndexOf("$testsInfo.commit -ne $commit", StringComparison.Ordinal) < publish.IndexOf("dotnet publish", StringComparison.Ordinal));
    }

    // Uma publicação que falha a meio não fica em publish\ com ar de pronta, e o pack.ps1 só empacota uma publicação
    // completa, da versão e do modo pedidos (o CI pede -NoSandbox explicitamente).
    [Fact]
    public void Pack_OnlyTakesACompletePublishOfTheRequestedVersionAndMode()
    {
        var publish = Read("build", "publish.ps1");
        Assert.Matches(@"trap \{\r?\n\s+if \(Test-Path -LiteralPath \$publishDir\) \{ Remove-Item -LiteralPath \$publishDir -Recurse", publish);
        Assert.DoesNotContain("exit $LASTEXITCODE", publish);
        // A marca é a última coisa que o publish.ps1 escreve.
        var mark = publish.LastIndexOf("\"eteris-build.json\"", StringComparison.Ordinal);
        Assert.True(mark > publish.LastIndexOf("throw ", StringComparison.Ordinal));
        var pack = Read("build", "pack.ps1");
        Assert.Contains("[switch]$NoSandbox", pack);
        Assert.Contains("publish\\eteris-build.json", pack);
        Assert.Contains("$build.version -ne $Version", pack);
        Assert.Contains("[bool]$build.sandbox -eq [bool]$NoSandbox", pack);
        Assert.True(pack.IndexOf("eteris-build.json", StringComparison.Ordinal) < pack.IndexOf("vpk pack", StringComparison.Ordinal));
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "build", "pack.ps1"));
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "build/pack.ps1 tem acentos: UTF-8 com BOM (PowerShell 5.1)");
    }

    // O login Google da 0.2.13 no fluxo da 0.2.14 (docs/sincronizacao.md): o JSON valida-se antes de compilar, a cópia
    // fica dentro do trap (um Copy-Item que falhe apaga publish\), depois da verificação da forma e do caminho do perfil
    // e antes da marca, e a marca diz se o levou sem guardar o client_id.
    [Fact]
    public void Publish_CopiesGoogleOAuthUnderTheTrapAndBeforeTheBuildMark()
    {
        var publish = Read("build", "publish.ps1").Replace("\r\n", "\n");
        Assert.Contains("[string]$GoogleOAuthJson = \"\"", PublishParam(publish));
        Assert.DoesNotContain("exit ", publish);
        var trap = publish.IndexOf("trap {", StringComparison.Ordinal);
        var validate = publish.IndexOf("ConvertFrom-Json } catch { throw \"-GoogleOAuthJson", StringComparison.Ordinal);
        var dotnet = publish.IndexOf("dotnet publish ", StringComparison.Ordinal);
        var shape = publish.IndexOf("Publicado COM sandbox", StringComparison.Ordinal);
        var copy = publish.IndexOf("Copy-Item -LiteralPath $GoogleOAuthJson -Destination (Join-Path $publishDir \"google-oauth.json\")", StringComparison.Ordinal);
        var mark = publish.LastIndexOf("\"eteris-build.json\"", StringComparison.Ordinal);
        Assert.True(trap >= 0 && validate > trap && dotnet > validate, "a validação do JSON vem antes de compilar, dentro do trap");
        Assert.True(copy > shape && copy > publish.LastIndexOf("throw ", StringComparison.Ordinal), "a cópia vem depois das verificações");
        Assert.True(mark > copy, "a marca vem depois da cópia");
        // O $oauth só se usa depois de validado: a primeira atribuição com valor é a do ConvertFrom-Json.
        Assert.Equal(publish.IndexOf("$oauth = $null", StringComparison.Ordinal), publish.IndexOf("$oauth", StringComparison.Ordinal));
        Assert.Single(Regex.Matches(publish, @"google-oauth\.json""\)"));
        Assert.Matches(@"google_oauth\s*=\s*\[bool\]\$oauth\r?\n", publish);
        Assert.DoesNotMatch(@"client_id\s*=", publish);
        // A limpeza antiga «nunca um google-oauth.json esquecido» já não faz falta: publish\ é apagada no início.
        Assert.DoesNotContain("$oauthTarget", publish);
    }

    // Decisão 2 do plano do sandbox da 0.2.14: uma release oficial leva o login Google. O pack.ps1 recusa, antes de
    // empacotar, uma publicação sem ele (a marca com google_oauth = false, ou sem o publish\google-oauth.json), a dizer
    // como publicar com -GoogleOAuthJson; -SemLogin empacota assim mesmo, com aviso (a publicação de emergência pelo CI,
    // que não tem o ficheiro), e é recusado numa publicação que o leva, como o -NoSandbox numa publicação com sandbox.
    [Fact]
    public void Pack_RefusesAPublishWithoutGoogleLogin_UnlessSemLogin()
    {
        var pack = Read("build", "pack.ps1");
        Assert.Contains("[switch]$SemLogin", pack);
        Assert.Contains("[bool]$build.google_oauth", pack);
        Assert.Contains("\"publish\\google-oauth.json\"", pack);
        var refusal = pack.IndexOf("if (-not $SemLogin) { throw \"publish\\ é uma publicação SEM login Google", StringComparison.Ordinal);
        Assert.True(refusal > 0, "pack.ps1 sem a recusa de uma publicação sem login Google");
        Assert.True(refusal < pack.IndexOf("vpk pack", StringComparison.Ordinal), "a recusa vem antes de empacotar");
        var message = pack[refusal..pack.IndexOf('\n', refusal)];
        Assert.Contains("build\\publish.ps1 -Version $Version -GoogleOAuthJson <caminho do google-oauth.json>", message);
        Assert.Contains("-SemLogin", message);
        Assert.Contains("Write-Warning \"Pacote SEM login Google (-SemLogin)", pack);
        Assert.Contains("throw \"publish\\ é uma publicação COM login Google e foi pedido -SemLogin\"", pack);
        // A marca e o ficheiro têm de bater, e isso vê-se antes de olhar para o -SemLogin.
        var mismatch = pack.IndexOf("if ($login -ne $loginFile) { throw \"publish\\ incoerente", StringComparison.Ordinal);
        Assert.True(mismatch > 0 && mismatch < pack.IndexOf("$SemLogin) { throw", StringComparison.Ordinal), "a pasta incoerente recusa-se antes do -SemLogin");
        // Só se vê se o ficheiro existe: o conteúdo (o client_id) nunca é lido nem escrito pelo pack.ps1.
        Assert.DoesNotMatch(@"(Get-Content|ReadAll\w+|Copy-Item|Move-Item)[^\r\n]*google-oauth", pack);
        Assert.DoesNotContain("client_id", pack);
        // O CI não tem o google-oauth.json: a publicação de emergência pede as duas exceções.
        Assert.Contains("pack.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox -SemLogin", Read(".github", "workflows", "release.yml"));
    }

    // O mesmo, a correr o pack.ps1 verdadeiro numa cópia com uma pasta publish\ falsa (só a marca): todos os casos são
    // recusas, que saem antes do vpk — e o PATH do processo fica só com o sistema, para o vpk nunca correr daqui.
    // «login»: a marca diz google_oauth; «file»: há um publish\google-oauth.json (vazio, feito aqui).
    [Theory]
    [InlineData(false, false, "", "SEM login Google")]
    // A marca e o ficheiro não dizem o mesmo (a pasta publish\ foi mexida à mão depois de publicar): recusa sempre, com
    // ou sem -SemLogin. Com ele, um google-oauth.json posto à mão (que o publish.ps1 nunca verificou) saía num pacote
    // anunciado como «sem login», e uma marca a dizer que o leva sem ele lá ia na instalação a mentir.
    [InlineData(true, false, "", "publish\\ incoerente")]
    [InlineData(false, true, "", "publish\\ incoerente")]
    [InlineData(true, false, "-SemLogin", "publish\\ incoerente")]
    [InlineData(false, true, "-SemLogin", "publish\\ incoerente")]
    [InlineData(true, true, "-SemLogin", "COM login Google e foi pedido -SemLogin")]
    // Com o login (ou sem ele e com -SemLogin) passa esta verificação e pára na seguinte: a pasta é COM sandbox.
    [InlineData(true, true, "-NoSandbox", "COM sandbox e foi pedido -NoSandbox")]
    [InlineData(false, false, "-NoSandbox -SemLogin", "COM sandbox e foi pedido -NoSandbox")]
    public async Task Pack_RunOnAFakePublish_RefusesBeforePacking(bool login, bool file, string switches, string expected)
    {
        var dir = Directory.CreateTempSubdirectory("eteris-pack-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "build"));
            Directory.CreateDirectory(Path.Combine(dir.FullName, "publish"));
            var script = Path.Combine(dir.FullName, "build", "pack.ps1");
            File.Copy(Path.Combine(Root(), "build", "pack.ps1"), script);
            File.WriteAllText(Path.Combine(dir.FullName, "publish", "eteris-build.json"),
                              "{ \"version\": \"9.9.9\", \"sandbox\": true, \"google_oauth\": " + (login ? "true" : "false") + " }");
            if (file) File.WriteAllText(Path.Combine(dir.FullName, "publish", "google-oauth.json"), "{}");
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            start.Environment["PATH"] = Environment.SystemDirectory;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Version", "9.9.9" })
                start.ArgumentList.Add(argument);
            foreach (var argument in switches.Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(argument);
            using var shell = Process.Start(start)!;
            var error = shell.StandardError.ReadToEndAsync();
            var output = await shell.StandardOutput.ReadToEndAsync();
            Assert.True(shell.WaitForExit(60_000), "powershell.exe não acabou em 60 s");
            // Sem a saída nas mensagens: traz o caminho da pasta temporária, com o perfil (o registo do CI é público).
            // E sem espaços nem quebras: o PowerShell parte as linhas do erro pela largura da consola.
            var all = Regex.Replace(output + await error, @"\s+", "");
            bool Said(string text) => all.Contains(Regex.Replace(text, @"\s+", ""), StringComparison.Ordinal);
            Assert.True(shell.ExitCode != 0, "o pack.ps1 não recusou");
            Assert.True(Said(expected), $"o pack.ps1 não recusou com «{expected}»");
            // Uma só recusa: a do login não aparece quando a publicação o leva ou o -SemLogin a aceita, e só ela diz como
            // publicar; o aviso do -SemLogin só aparece quando ele foi preciso e a publicação é mesmo sem login.
            Assert.True(Said("-GoogleOAuthJson") == (expected == "SEM login Google"), "a recusa do login (e só ela) diz como publicar com ele");
            Assert.True(Said("Pacote SEM login Google (-SemLogin)") == (switches.Contains("-SemLogin") && !login && !file), "o aviso do -SemLogin");
            Assert.True(Said("publish\\ incoerente") == (login != file), "a recusa da pasta incoerente");
            Assert.False(Directory.Exists(Path.Combine(dir.FullName, "Releases")), "o pack.ps1 empacotou");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void BuildAll_IsReadableByWindowsPowerShell_AndProducesWhatPublishReads()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "build", "sandbox", "build-all.ps1"));
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.True(bom || bytes.All(b => b < 0x80), "build/sandbox/build-all.ps1 tem de ser ASCII ou UTF-8 com BOM (PowerShell 5.1)");
        var script = Encoding.UTF8.GetString(bytes);
        foreach (var piece in new[] { "prepare-source.ps1", "build-cefsharp.cmd", "pack-local.ps1", @"build\sandbox\client\build.cmd",
                                      "verify-client.ps1", "windows-sandbox-info.patch", "eteris-cefsharp.json", "EterisCefSharpFeed" })
            Assert.Contains(piece, script);
        Assert.Contains("Join-Path $repo 'build\\cefsharp'", script);
        Assert.Contains("Join-Path $cefsharpDir 'feed'", script);
        Assert.Contains("Join-Path $clientOut 'Release\\Eteris.Browser.dll'", script);
        Assert.Contains("Join-Path $repo 'build\\sandbox\\client\\out'", script);
        // Fonte fixa: a tag do CefSharp e o commit para que aponta.
        Assert.Contains("$CefSharpTag = 'v156.0.150'", script);
        Assert.Matches(@"\$CefSharpCommit = '[0-9a-f]{40}'", script);
    }

    // A marca dos testes (tests-ok.json) só existe depois de uma corrida com os testes aprovados: é apagada no princípio
    // e escrita depois dos testes, nunca com -SkipTests; e o caminho do repositório com acentos passa ao nome curto.
    [Fact]
    public void BuildAll_MarksTheTestsOnlyAfterTheyPass()
    {
        var script = Read("build", "sandbox", "build-all.ps1");
        Assert.Contains("$testsOk = Join-Path $outDir 'tests-ok.json'", script);
        var delete = script.IndexOf("if (Test-Path -LiteralPath $testsOk) { [IO.File]::Delete($testsOk) }", StringComparison.Ordinal);
        var tests = script.IndexOf("Run $log 'dotnet' @('test'", StringComparison.Ordinal);
        var write = script.IndexOf("[IO.File]::WriteAllText($testsOk", StringComparison.Ordinal);
        Assert.True(delete > 0 && tests > delete && write > tests, "build-all.ps1: apagar a marca, testar, só depois escrevê-la");
        var skip = Regex.Match(script, @"if \(\$SkipTests\) \{(?<skip>.*?)\} else \{", RegexOptions.Singleline);
        Assert.True(skip.Success);
        Assert.DoesNotContain("$testsOk", skip.Groups["skip"].Value);
        Assert.Contains("'[^\\x00-\\x7F]'", script);
        Assert.Contains(".ShortPath", script);
        // O Remove-Item -LiteralPath do PowerShell 5.1 falha com caminhos 8.3.
        Assert.DoesNotContain("Remove-Item -LiteralPath", script);
    }

    // O CefSharp que o build-all compila é o mesmo que o projeto usa (uma atualização do CefSharp mexe nos dois).
    [Fact]
    public void BuildAll_CompilesTheCefSharpVersionOfTheProject()
    {
        var project = XDocument.Load(Path.Combine(Root(), "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        var official = project.Descendants("PackageReference").Single(p => p.Attribute("Include")?.Value == "CefSharp.Wpf.NETCore").Attribute("Version")!.Value;
        Assert.Contains($"$Official = '{official}'", Read("build", "sandbox", "build-all.ps1"));
        Assert.Contains($"$CefSharpTag = 'v{official}'", Read("build", "sandbox", "build-all.ps1"));
    }

    [Fact]
    public void SandboxBuildOutputs_AreNeverCommitted()
    {
        var ignore = File.ReadAllLines(Path.Combine(Root(), ".gitignore"));
        foreach (var dir in new[] { "build/cefsharp/src/", "build/cefsharp/feed/", "build/sandbox/out/", "build/sandbox/client/out/" })
            Assert.Contains(dir, ignore);
    }

    // Dentro do repositório o clone do CefSharp apanharia o Directory.Build.props do Éteris (avisos como erros). Os dois
    // do clone só trazem as opções dos caminhos de quem compila (BuilderPathsTests).
    [Fact]
    public void PrepareSource_IsolatesTheCloneFromTheEterisBuildRules()
    {
        var prepare = Read("build", "cefsharp", "prepare-source.ps1");
        Assert.Contains("'Directory.Build.props'   = '<Project>'", prepare);
        Assert.Contains("'Directory.Build.targets' = '<Project>'", prepare);
        Assert.Contains("foreach ($stub in $stubs.Keys) {", prepare);
    }
}
