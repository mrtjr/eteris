using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Guardas dos fluxos do GitHub Actions que só correm ao publicar (um erro aqui só aparecia no dia da versão).
public class WorkflowTests
{
    private static string Workflow(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", name));

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
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
        Assert.Equal(["verify", "ensaio", "publish-sem-codecs"], jobs);

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
        // A emergência também sobe em rascunho: quem publica é o build/release.ps1, com a chave que o CI não tem (CAD-02).
        Assert.DoesNotContain("--publish", publish);
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

    // A publicação de emergência corre os passos de hoje sobre a árvore da etiqueta, e o setup-dotnet lê o global.json
    // dela. As etiquetas até à v0.2.16 não o têm: o job pára logo depois do checkout, com uma mensagem que diz o que
    // fazer (publicar de uma etiqueta que o tenha), em vez de falhar no setup-dotnet sem explicação.
    [Fact]
    public void Release_PublishSemCodecs_StopsEarlyWhenTheTagHasNoGlobalJson()
    {
        var publish = Job(Workflow("release.yml").Replace("\r\n", "\n"), "publish-sem-codecs");
        var checkout = publish.IndexOf("actions/checkout@", StringComparison.Ordinal);
        var guard = publish.IndexOf("Test-Path global.json", StringComparison.Ordinal);
        var setup = publish.IndexOf("actions/setup-dotnet@", StringComparison.Ordinal);
        Assert.True(checkout > 0 && setup > 0, "publish-sem-codecs sem checkout ou setup-dotnet");
        Assert.True(guard > checkout && guard < setup, "publish-sem-codecs: falta conferir o global.json da etiqueta entre o checkout e o setup-dotnet");
        var message = Regex.Match(publish[guard..setup], @"throw ""([^""]*)""");
        Assert.True(message.Success, "a guarda do global.json não lança uma mensagem");
        Assert.Contains("global.json", message.Groups[1].Value);
        // Só ASCII: a página de códigos do runner troca os acentos (TFI-01).
        Assert.True(message.Groups[1].Value.All(c => c < 128), "mensagem da guarda fora do ASCII: " + message.Groups[1].Value);
    }

    // CAD-05: o vpk tem a versão do pacote Velopack (o 1.2.161 do projeto), e não a última que houver no dia da emergência.
    [Fact]
    public void Release_PinsVpkToTheVelopackPackageVersion()
    {
        var yml = Workflow("release.yml").Replace("\r\n", "\n");
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        var version = Regex.Match(csproj, "<PackageReference Include=\"Velopack\" Version=\"([^\"]+)\"").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        var installs = Regex.Matches(yml, @"dotnet tool install -g vpk.*").Select(m => m.Value.TrimEnd()).ToList();
        Assert.Equal(2, installs.Count);
        Assert.All(installs, line => Assert.Equal("dotnet tool install -g vpk --version " + version, line));
    }

    // CAD-05: a publicação de emergência (pelo CI, sem codecs nem sandbox) nunca era vista a funcionar antes do dia em
    // que fazia falta. Cada etiqueta ensaia os mesmos passos até ao pacote, sem enviar nada nem ter a escrita.
    [Fact]
    public void Release_Rehearsal_RunsTheEmergencyStepsWithoutUploading()
    {
        var yml = Workflow("release.yml").Replace("\r\n", "\n");
        var ensaio = Job(yml, "ensaio");
        Assert.Contains("if: github.event_name == 'push'", ensaio);
        Assert.Contains("timeout-minutes: 60", ensaio);
        Assert.Contains("publish.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox", ensaio);
        Assert.Contains("pack.ps1 -Version $env:RELEASE_TAG.TrimStart(\"v\") -NoSandbox -SemLogin", ensaio);
        Assert.Contains("EterisBrowser-win-Setup.exe", ensaio);
        // Um \b a mais num caminho do Windows (.\build) vira um backspace no YAML e o passo parte no CI.
        Assert.DoesNotContain(yml, c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
        Assert.Contains(@".\build\pack.ps1 -Version", ensaio);
        foreach (var no in new[] { "vpk upload", "vpk download", "contents: write", "secrets." })
            Assert.DoesNotContain(no, ensaio);
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

    // CAD-09: o vpk instalado no CI é o da versão do Velopack do projeto (um vpk mais novo podia empacotar o que o
    // cliente não sabe ler), e o token chega aos comandos vpk pelo ambiente do passo, nunca escrito na linha de comando.
    [Fact]
    public void Release_InstallsThePinnedVpk()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        var velopack = Regex.Match(csproj, @"<PackageReference Include=""Velopack"" Version=""([^""]+)"" />");
        Assert.True(velopack.Success, "Eteris.Browser.csproj sem o PackageReference do Velopack");

        var publish = Job(Workflow("release.yml").Replace("\r\n", "\n"), "publish-sem-codecs");
        Assert.Matches(new Regex(@"^\s+- run: dotnet tool install -g vpk --version " + Regex.Escape(velopack.Groups[1].Value) + "$", RegexOptions.Multiline), publish);

        var steps = Regex.Split(publish, @"(?=^      - )", RegexOptions.Multiline);
        foreach (var command in new[] { "vpk download github", "vpk upload github" })
        {
            var step = Assert.Single(steps, s => s.Contains(command, StringComparison.Ordinal));
            Assert.Contains("--token $env:RELEASE_TOKEN", step);
            Assert.Matches(new Regex(@"^        env:\n          RELEASE_TOKEN: \$\{\{ secrets\.GITHUB_TOKEN \}\}$", RegexOptions.Multiline), step);
        }
    }

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

    // CAD-09: uma etiqueta («@v4») pode passar a apontar para outro código sem aviso; cada ação fica fixada por SHA, com
    // a versão em comentário (o Dependabot sobe-as, um pedido de cada vez). Permissões mínimas no nível de cima, os
    // checkouts sem a credencial guardada no .git, e nenhum segredo numa linha de comando (fica nos registos do job e
    // na linha de comandos do processo; passa pelo ambiente).
    [Theory]
    [InlineData("release.yml")]
    [InlineData("build.yml")]
    [InlineData("pages.yml")]
    [InlineData("stress.yml")]
    [InlineData("e2e.yml")]
    public void Workflows_PinActionsAndKeepPermissionsMinimal(string name)
    {
        var yml = Workflow(name).Replace("\r\n", "\n");
        var lines = yml.Split('\n');
        var problems = new List<string>();
        var pinned = new Regex(@"uses: [\w.-]+/[\w./-]+@[0-9a-f]{40} # v\d+(\.\d+)*$");
        var runIndent = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = line.Length - line.TrimStart().Length;
            if (runIndent >= 0 && (line.Trim().Length == 0 || indent > runIndent))
            {
                if (line.Contains("secrets.", StringComparison.Ordinal)) problems.Add($"{i + 1}: segredo na linha de comando");
                continue;
            }
            runIndent = -1;
            if (line.Contains("uses:", StringComparison.Ordinal) && !pinned.IsMatch(line))
                problems.Add($"{i + 1}: ação sem SHA de 40 hexadecimais e versão em comentário");
            if (line.Contains("actions/checkout@", StringComparison.Ordinal)
                && !lines.Skip(i + 1).Take(4).Any(l => l.Trim() == "persist-credentials: false"))
                problems.Add($"{i + 1}: checkout sem persist-credentials: false");
            var run = Regex.Match(line, @"^(\s*)(?:- )?run:\s*(.*)$");
            if (run.Success)
            {
                if (run.Groups[2].Value.Contains("secrets.", StringComparison.Ordinal)) problems.Add($"{i + 1}: segredo na linha de comando");
                if (run.Groups[2].Value.StartsWith('|') || run.Groups[2].Value.StartsWith('>'))
                    runIndent = line.IndexOf("run:", StringComparison.Ordinal);
            }
        }
        Assert.Matches(new Regex("^permissions:\n  contents: read$", RegexOptions.Multiline), yml);
        Assert.True(problems.Count == 0, name + ":\n" + string.Join("\n", problems));
    }

    // O build.yml corria duas vezes por pedido (push no ramo e pull_request): passa a correr no push do main e nos pedidos.
    [Fact]
    public void Build_RunsOncePerChange()
    {
        var yml = Workflow("build.yml").Replace("\r\n", "\n");
        Assert.Contains("push:\n    branches: [main]", yml);
        Assert.Contains("pull_request:", yml);
    }

    private static string Job(string yml, string name)
        => Regex.Match(yml, "^  " + Regex.Escape(name) + @":\n(.*?)(?=^  \S|\z)", RegexOptions.Multiline | RegexOptions.Singleline).Groups[1].Value;
}
