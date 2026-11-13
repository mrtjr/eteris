using System.IO;
using System.Xml.Linq;

namespace Eteris.Browser.Tests;

// Motor com codecs (docs/motor.md, «Motor com codecs»): o csproj só troca o runtime da CEF quando existe a compilação
// própria (com manifesto). Sem a pasta, o Éteris tem de continuar a usar o runtime do NuGet, sem nada a mudar.
public class CefCodecsRuntimeTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static XDocument Project() => XDocument.Load(Path.Combine(Root(), "src", "Eteris.Browser", "Eteris.Browser.csproj"));

    [Fact]
    public void RuntimeSwap_OnlyWithTheOwnBuildAndItsManifest()
    {
        var on = Project().Descendants("EterisCefRuntimeOn").Single();
        var condition = on.Attribute("Condition")!.Value;
        Assert.Contains(@"Exists('$(EterisCefRuntime)\libcef.dll')", condition);
        Assert.Contains(@"Exists('$(EterisCefRuntime)\manifest.json')", condition);
        foreach (var name in new[] { "EterisCefRuntimeAfterBuild", "EterisCefRuntimeAfterPublish" })
        {
            var target = Project().Descendants("Target").Single(t => t.Attribute("Name")?.Value == name);
            Assert.Contains("'$(EterisCefRuntimeOn)' == 'true'", target.Attribute("Condition")!.Value);
        }
    }

    // O bootstrap.exe/bootstrapc.exe (sandbox, passo D) ainda não é usado: não vai na saída. O manifesto vai para junto
    // dos avisos de licença: é a marca que Tabs/PlatformCodecs.cs procura.
    [Fact]
    public void RuntimeSwap_LeavesOutTheBootstrapAndShipsTheManifestWithTheLicenses()
    {
        var items = Project().Descendants("Target").Single(t => t.Attribute("Name")?.Value == "EterisCefRuntimeItems");
        var file = items.Descendants("_EterisCefFile").Single().Attribute("Exclude")!.Value;
        Assert.Contains(@"$(EterisCefRuntime)\bootstrap.exe", file);
        Assert.Contains(@"$(EterisCefRuntime)\bootstrapc.exe", file);
        Assert.Contains(@"$(EterisCefRuntime)\manifest.json", file);
        Assert.Contains(@"$(EterisCefRuntime)\manifest.json", items.Descendants("_EterisCefLicense").Single().Attribute("Include")!.Value);
        Assert.Contains(@"licenses\cef-codecs\", Project().ToString());
        Assert.Equal(Path.Combine("licenses", "cef-codecs", "manifest.json"), Eteris.Browser.Tabs.PlatformCodecs.MarkerPath);
    }

    // As fontes do motor (script e patches) vão no repositório byte a byte como foram validadas: o SHA256 dos patches
    // está no manifest.json do runtime, e o script só tem ASCII, como os outros .ps1 (o PowerShell 5 lê-os em ANSI).
    [Fact]
    public void Sources_AreVersionedVerbatim()
    {
        var dir = Path.Combine(Root(), "build", "cef-codecs");
        Assert.True(File.Exists(Path.Combine(dir, "README.md")));
        var script = File.ReadAllBytes(Path.Combine(dir, "build.ps1"));
        var bom = script.Length >= 3 && script[0] == 0xEF && script[1] == 0xBB && script[2] == 0xBF;
        Assert.True(bom || script.All(b => b < 0x80), "build/cef-codecs/build.ps1 tem de ser ASCII ou UTF-8 com BOM");
        foreach (var patch in new[] { "platform-codecs.patch", "ffmpeg-adts-demuxer.patch" })
            Assert.True(File.Exists(Path.Combine(dir, "patches", patch)), patch);
        var attributes = File.ReadAllLines(Path.Combine(Root(), ".gitattributes"));
        Assert.Contains("build/cef-codecs/patches/*.patch -text", attributes);
        Assert.Contains("build/cef-codecs/build.ps1 -text", attributes);
    }

    [Fact]
    public void OwnBuild_IsNeverCommitted()
        => Assert.Contains("build/cef-codecs/out/", File.ReadAllLines(Path.Combine(Root(), ".gitignore")));

    [Fact]
    public void Publish_RefusesAnythingButPlatformCodecs()
    {
        var publish = File.ReadAllText(Path.Combine(Root(), "build", "publish.ps1"));
        Assert.Contains("platform-codecs", publish);
        Assert.Contains("156.0.15+gcf671c0+chromium-156.0.8078.80", publish);
        Assert.Contains("sha256", publish);
        Assert.Contains("cf671c072925b27823a61181152f4f288e8e4306", publish);
    }

    // CAD-08: antes de compilar, o Check recusa uma árvore do motor com alterações fora dos patches e um FFmpeg que não é o
    // do código-fonte oferecido (licenses/FFmpeg-SOURCE.txt).
    [Fact]
    public void Check_RefusesChangesOutsideThePatches()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "build", "cef-codecs", "build.ps1"));
        Assert.Contains("status --porcelain --untracked-files=no", script);
        Assert.Matches(@"\$FfmpegCommit\s*=\s*'9db86ce5b5b454dfc96c435f9f0723012980c37f'", script);
        Assert.Contains("alteracoes fora dos patches", script);
        Assert.True(script.All(char.IsAscii), "build/cef-codecs/build.ps1 continua so com ASCII");
        Assert.Contains("9db86ce5b5b454dfc96c435f9f0723012980c37f", File.ReadAllText(Path.Combine(Root(), "licenses", "FFmpeg-SOURCE.txt")));
    }
}
