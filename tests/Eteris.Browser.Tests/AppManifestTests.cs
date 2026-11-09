using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

namespace Eteris.Browser.Tests;

// E2 do plano de desempenho da 0.2.14: o processo principal declara o Windows 10/11. Sem o <supportedOS>, o GetVersionEx
// devolve 6.2 e o Chromium julga-se no Windows 8 (os /prefetch: dos filhos do intervalo antigo, caminhos «>= WIN10»
// desligados). Só a compatibilidade muda: o nível de execução fica o do manifesto por omissão e o DPI fica com o WPF.
public class AppManifestTests
{
    private const string Windows10 = "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}";
    private static readonly XNamespace Asm = "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace Compatibility = "urn:schemas-microsoft-com:compatibility.v1";

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static XDocument Manifest() => XDocument.Load(Path.Combine(Root, "src", "Eteris.Browser", "app.manifest"));

    [Fact]
    public void Project_UsesTheManifest()
    {
        var project = XDocument.Load(Path.Combine(Root, "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        Assert.Contains(project.Descendants("ApplicationManifest"), e => e.Value.Trim() == "app.manifest");
    }

    [Fact]
    public void Manifest_DeclaresWindows10And11()
        => Assert.Contains(Manifest().Descendants(Compatibility + "supportedOS"), e => (string?)e.Attribute("Id") == Windows10);

    [Fact]
    public void Manifest_KeepsAsInvoker()
    {
        var level = Manifest().Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel");
        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
    }

    // O WPF trata da consciência de DPI; mudar isso aqui seria outro risco (e outra experiência).
    [Fact]
    public void Manifest_LeavesDpiToWpf()
        => Assert.DoesNotContain(Manifest().Descendants(), e => e.Name.LocalName is "dpiAware" or "dpiAwareness" or "windowsSettings");

    [Fact]
    public void Manifest_IsOnlyAssemblyTrustAndCompatibility()
        => Assert.All(Manifest().Root!.Elements(), e => Assert.Contains(e.Name.LocalName, new[] { "assemblyIdentity", "trustInfo", "compatibility" }));

    // O exe compilado leva o manifesto (o apphost copia os recursos Win32 da DLL): o GUID do Windows 10 está nos recursos.
    [Fact]
    public void BuiltExe_CarriesTheWindows10Guid()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Eteris.Browser.exe");
        Assert.True(File.Exists(exe), "Eteris.Browser.exe não está ao lado dos testes");
        using var stream = File.OpenRead(exe);
        using var pe = new PEReader(stream);
        var resources = pe.PEHeaders.PEHeader!.ResourceTableDirectory;
        Assert.True(resources.Size > 0, "o exe não tem recursos Win32");
        var image = File.ReadAllBytes(exe);
        Assert.Contains(Windows10, Encoding.UTF8.GetString(image), StringComparison.OrdinalIgnoreCase);
    }
}
