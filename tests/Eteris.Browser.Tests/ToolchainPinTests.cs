using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// CAD-10 (ARQ-15): o CI e a máquina de publicação compilam com as mesmas ferramentas. O SDK do .NET fica no
// global.json (os fluxos instalam o que ele diz) e o cargo corre sempre com --locked: um Cargo.lock desatualizado dá
// erro em vez de ser reescrito em silêncio. A versão do Rust (rust-toolchain.toml) fica para quando a toolchain
// fixada estiver instalada na máquina de publicação.
public class ToolchainPinTests
{
    [Fact]
    public void Sdk_IsPinnedByGlobalJson()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "global.json")));
        var sdk = json.RootElement.GetProperty("sdk");
        Assert.Matches(@"^10\.0\.\d{3}$", sdk.GetProperty("version").GetString());
        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());
        foreach (var (name, yml) in Workflows())
        {
            if (!yml.Contains("actions/setup-dotnet@", StringComparison.Ordinal)) continue;
            Assert.True(yml.Contains("global-json-file: global.json", StringComparison.Ordinal), name + ": setup-dotnet sem global-json-file");
            Assert.False(yml.Contains("dotnet-version:", StringComparison.Ordinal), name + ": dotnet-version ao lado do global.json");
        }
    }

    [Fact]
    public void Cargo_RunsLocked()
    {
        var missing = new List<string>();
        foreach (var (name, yml) in Workflows())
        {
            foreach (var line in yml.Split('\n'))
                if (line.Contains("cargo test", StringComparison.Ordinal) && !line.Contains("--locked", StringComparison.Ordinal))
                    missing.Add(name + ": " + line.Trim());
        }
        var csproj = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Eteris.Browser.csproj"));
        var build = Regex.Match(csproj, @"^.*build --release.*$", RegexOptions.Multiline);
        Assert.True(build.Success, "Eteris.Browser.csproj sem o «build --release» do alvo BuildShields");
        if (!build.Value.Contains("--locked", StringComparison.Ordinal)) missing.Add("Eteris.Browser.csproj: " + build.Value.Trim());
        Assert.True(missing.Count == 0, "cargo sem --locked:\n" + string.Join("\n", missing));
    }

    private static IEnumerable<(string Name, string Text)> Workflows()
        => Directory.EnumerateFiles(Path.Combine(Root(), ".github", "workflows"), "*.yml")
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f).Replace("\r\n", "\n", StringComparison.Ordinal)));

    private static string Root([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
