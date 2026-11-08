using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Eteris.Browser.Tests;

// Desde a 0.2.12 a publicação sai com o sandbox ligado por omissão (docs/sandbox.md, «Como publicar com o sandbox»):
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

    [Fact]
    public void Publish_IsWithSandboxByDefault_AndNoSandboxIsTheEmergencyExit()
    {
        var publish = Read("build", "publish.ps1");
        var param = Regex.Match(publish, @"^param\((.*)\)\r?$", RegexOptions.Multiline).Groups[1].Value;
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

    // Dentro do repositório o clone do CefSharp apanharia o Directory.Build.props do Éteris (avisos como erros).
    [Fact]
    public void PrepareSource_IsolatesTheCloneFromTheEterisBuildRules()
    {
        var prepare = Read("build", "cefsharp", "prepare-source.ps1");
        Assert.Contains("'Directory.Build.props', 'Directory.Build.targets'", prepare);
    }
}
