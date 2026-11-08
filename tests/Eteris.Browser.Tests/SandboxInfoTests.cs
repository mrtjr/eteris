using System.IO;
using System.Xml.Linq;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// Passo D de docs/sandbox.md, lado .NET: o ponteiro do sandbox só vem da DLL nativa já carregada pelo bootstrap; sem
// ela (apphost, testes) fica zero e o motor corre como sempre. O CefSharp do Éteris (patch v2) só entra com o feed local.
public class SandboxInfoTests
{
    [Theory]
    [InlineData(@"C:\Eteris\Eteris.Browser.exe", @"C:\Eteris\Eteris.Browser.dll")]
    [InlineData(@"C:\x\EterisSB.exe", @"C:\x\EterisSB.dll")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ClientDll_HasTheNameOfTheExecutable(string? processPath, string? expected)
        => Assert.Equal(expected, SandboxInfo.ClientDllPath(processPath));

    [Fact]
    public void NoSandboxSwitch_DisablesIt()
    {
        Assert.True(SandboxInfo.Disabled(["https://example.org", "--ETERIS-NO-SANDBOX"]));
        Assert.False(SandboxInfo.Disabled(["https://example.org/?q=--eteris-no-sandbox"]));
    }

    // Nunca carrega uma DLL: um módulo que não está carregado (ou não exporta EterisSandboxInfo) dá zero.
    [Fact]
    public void Read_IsZeroWithoutTheBootstrapClient()
    {
        var notLoaded = Path.Combine(Path.GetTempPath(), "eteris-nao-existe", "EterisSB.exe");
        Assert.Equal(0, SandboxInfo.Read(notLoaded, []));
        // O processo de testes (apphost do .NET): o seu .dll é uma montagem gerida, sem a exportação.
        Assert.Equal(0, SandboxInfo.Read(Environment.ProcessPath, []));
        Assert.Equal(0, SandboxInfo.Pointer);
        Assert.False(CefConfigurator.SandboxEnabled);
    }

    [Theory]
    [InlineData("9334", 9334)]
    [InlineData(" 9222 ", 9222)]
    [InlineData("80", null)]
    [InlineData("70000", null)]
    [InlineData("-9334", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DevToolsPort_OnlyAValidUnprivilegedPort(string? value, int? expected)
        => Assert.Equal(expected, TestSwitches.Port(value));

    [Fact]
    public void Build_OpensDevToolsOnlyWhenAsked()
    {
        Assert.Equal(0, CefConfigurator.Build("p").RemoteDebuggingPort);
        Assert.Equal(9334, CefConfigurator.Build("p", devToolsPort: 9334).RemoteDebuggingPort);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    // Sem o feed local o projeto usa o CefSharp oficial e não define ETERIS_CEFSHARP_SANDBOX (a CI continua igual).
    [Fact]
    public void Project_UsesThePatchedCefSharpOnlyWithTheLocalFeed()
    {
        var project = XDocument.Load(Path.Combine(Root(), "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        var references = project.Descendants("PackageReference")
            .Where(p => (p.Attribute("Include") ?? p.Attribute("Update"))!.Value == "CefSharp.Wpf.NETCore").ToList();
        Assert.Equal("156.0.150", references.Single(p => p.Attribute("Include") is not null).Attribute("Version")!.Value);
        var update = references.Single(p => p.Attribute("Update") is not null);
        Assert.Equal("156.0.150-eteris.$(EterisCefSharpN)", update.Attribute("Version")!.Value);
        Assert.Equal("'$(EterisCefSharpFeed)' != ''", update.Parent!.Attribute("Condition")!.Value);
        var define = project.Descendants("DefineConstants").Single();
        Assert.Equal("'$(EterisCefSharpFeed)' != ''", define.Attribute("Condition")!.Value);
        Assert.Contains("ETERIS_CEFSHARP_SANDBOX", define.Value);
    }

    // Pelo bootstrap mas sem sandbox (--eteris-no-sandbox, CefSharp oficial): os filhos são o próprio exe, não o processo
    // filho .NET (que a publicação com sandbox não traz). Sem bootstrap, o de sempre.
    [Fact]
    public void Build_ChildrenAreTheBootstrapWhenThereIsNoSandbox()
    {
        const string exe = @"C:\Eteris\Eteris.Browser.exe";
        Assert.Equal(exe, CefConfigurator.Build("p", bootstrapExe: exe).BrowserSubprocessPath);
        Assert.EndsWith("CefSharp.BrowserSubprocess.exe", CefConfigurator.Build("p").BrowserSubprocessPath, StringComparison.OrdinalIgnoreCase);
        // Os testes compilam com o CefSharp oficial: o ponteiro é ignorado e os filhos continuam a ser o bootstrap.
        Assert.False(CefConfigurator.SandboxSupported);
        Assert.Equal(exe, CefConfigurator.Build("p", sandboxInfo: 0x1234, bootstrapExe: exe).BrowserSubprocessPath);
    }

    // Opção B de docs/sandbox.md: com -p:EterisSandbox=true o bootstrap fica com o nome de sempre (Velopack, atalhos,
    // instância única) e o .NET passa a Eteris.App; a publicação deixa de trazer o processo filho .NET e o apphost.
    [Fact]
    public void Project_SandboxLayoutKeepsEterisBrowserExeAsTheEntryPoint()
    {
        var project = XDocument.Load(Path.Combine(Root(), "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        var sandboxProps = project.Descendants("PropertyGroup").Single(g => g.Attribute("Condition")?.Value == "'$(EterisSandbox)' == 'true'");
        Assert.Equal("Eteris.App", sandboxProps.Element("AssemblyName")!.Value);
        var entries = project.Descendants("_EterisSandboxEntry").ToDictionary(e => e.Attribute("TargetName")!.Value, e => e.Attribute("Include")!.Value);
        Assert.Equal(@"$(EterisCefRuntime)\bootstrap.exe", entries["Eteris.Browser.exe"]);
        Assert.Equal("$(EterisSandboxClientDll)", entries["Eteris.Browser.dll"]);
        var publish = project.Descendants("Target").Single(t => t.Attribute("Name")?.Value == "EterisSandboxAfterPublish");
        var deleted = publish.Element("Delete")!.Attribute("Files")!.Value;
        Assert.Contains("$(PublishDir)$(AssemblyName).exe", deleted);
        Assert.Contains("$(PublishDir)CefSharp.BrowserSubprocess.exe", deleted);
        // O bootstrap leva o ícone e a versão do Éteris (build/sandbox/brand-bootstrap.ps1), depois de copiado.
        Assert.Contains("brand-bootstrap.ps1", publish.Element("Exec")!.Attribute("Command")!.Value);
        Assert.True(publish.Elements().ToList().IndexOf(publish.Element("Copy")!) < publish.Elements().ToList().IndexOf(publish.Element("Exec")!));
        var check = project.Descendants("Target").Single(t => t.Attribute("Name")?.Value == "EterisSandboxCheck");
        Assert.Equal(3, check.Elements("Error").Count());
    }

    // Com o .NET renomeado (Eteris.App), um pack URI com o nome da montagem escrito à mão deixaria de encontrar o
    // recurso: usa-se o nome da montagem em execução (typeof(MainWindow).Assembly).
    [Fact]
    public void PackUris_DoNotNameTheAssembly()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Eteris.Browser"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            Assert.DoesNotContain("Eteris.Browser;component", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
