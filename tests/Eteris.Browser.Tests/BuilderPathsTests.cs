using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Eteris.Browser.Tests;

// Os binários publicados não levam o caminho do perfil de quem compila (docs/sandbox.md, «Caminhos de quem compila»;
// arrumação 0.2.14, fase S4). As peças nativas do sandbox que o build/sandbox/build-all.ps1 compila ficam limpas por três
// opções (o __FILE__ da DLL cliente, o caminho do PDB do CefSharp.Core.Runtime e o PDB embutido do CefSharp.Core), e o
// build-all.ps1 e o publish.ps1 conferem-nas com a mesma procura (build/profile-paths.ps1). Nenhum nome está escrito
// aqui: o teste da procura usa o perfil da máquina onde corre.
public class BuilderPathsTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    // DLL cliente: o __FILE__ do libcef_dll_wrapper e dos cabeçalhos da CEF leva o caminho da distribuição. O
    // /d1trimfile tira-o, com o prefixo do CEF_ROOT, em todas as unidades de compilação (por isso antes do wrapper e da
    // DLL), só no C++ e sem a barra final.
    [Fact]
    public void ClientDll_TrimsTheCefRootFromFile()
    {
        var cmake = Read("build", "sandbox", "client", "CMakeLists.txt");
        var trim = cmake.IndexOf("add_compile_options(\"$<$<COMPILE_LANGUAGE:CXX>:/d1trimfile:${_eteris_trim_prefix}>\")", StringComparison.Ordinal);
        Assert.True(trim > 0, "CMakeLists.txt sem o /d1trimfile só no C++");
        Assert.Contains("string(REGEX REPLACE \"/+$\" \"\" _eteris_trim_prefix \"${CEF_ROOT}\")", cmake);
        Assert.Contains("file(TO_NATIVE_PATH \"${_eteris_trim_prefix}\" _eteris_trim_prefix)", cmake);
        // Depois de o CEF_ROOT ficar com barras normais e antes do wrapper e da DLL.
        Assert.True(cmake.IndexOf("file(TO_CMAKE_PATH \"${CEF_ROOT}\" CEF_ROOT)", StringComparison.Ordinal) < trim);
        Assert.True(trim < cmake.IndexOf("add_subdirectory(${CEF_LIBCEF_DLL_WRAPPER_PATH}", StringComparison.Ordinal));
        Assert.True(trim < cmake.IndexOf("add_library(eteris_client", StringComparison.Ordinal));
        // Um só prefixo; e nada de /experimental:deterministic (/pathmap), que com o /WX da CEF partiria a compilação.
        Assert.Single(Regex.Matches(cmake, @"^\s*add_compile_options\(.*d1trimfile", RegexOptions.Multiline));
        Assert.DoesNotMatch(@"(?m)^[^#]*(/pathmap|experimental:deterministic)", cmake);
    }

    // O texto de um dos Directory.Build.* que o prepare-source.ps1 escreve no clone do CefSharp: as linhas são literais
    // entre plicas (com '' para uma plica), cada uma seguida de "`n".
    public static string CloneStub(string prepare, string name)
    {
        var start = prepare.IndexOf($"'{name}'", StringComparison.Ordinal);
        Assert.True(start >= 0, $"prepare-source.ps1 sem {name}");
        var body = prepare[(start + name.Length + 2)..];
        var end = Regex.Match(body, @"'Directory\.Build\.[a-z]+'\s*=|\r?\n\s*\}");
        if (end.Success) body = body[..end.Index];
        var lines = Regex.Matches(body, "'((?:[^']|'')*)'").Select(m => m.Groups[1].Value.Replace("''", "'"));
        return string.Join("\n", lines) + "\n";
    }

    [Fact]
    public void CloneStub_ReadsTheLiteralLinesOfOneFile()
    {
        const string script = "  $stubs = [ordered]@{\n    'A.props'   = '<Project>' + \"`n\" +\n"
                              + "                  '  <X C=\"''a'' == ''b''\" />' + \"`n\" +\n                  '</Project>' + \"`n\"\n"
                              + "    'Directory.Build.targets' = '<Other />' + \"`n\"\n  }\n";
        Assert.Equal("<Project>\n  <X C=\"'a' == 'b'\" />\n</Project>\n", CloneStub(script, "A.props"));
        Assert.Equal("<Other />\n", CloneStub(script, "Directory.Build.targets"));
    }

    // CefSharp: o .vcxproj liga com /PDBALTPATH:%_PDB% (a entrada CodeView do CefSharp.Core.Runtime.dll fica só com o
    // nome do .pdb), acrescentado às opções do próprio projeto; os .csproj com DeterministicSourcePaths (o PDB embutido
    // do CefSharp.Core.dll fica com /_/ em vez do clone). Os dois ficheiros continuam a parar a procura das regras do
    // Éteris, e o prepare-source.ps1 continua só em ASCII (o PowerShell 5.1 lê-o sem BOM como ANSI).
    [Fact]
    public void PrepareSource_WritesThePathOptionsIntoTheClone()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "build", "cefsharp", "prepare-source.ps1"));
        Assert.True(bytes.All(b => b < 0x80), "build/cefsharp/prepare-source.ps1 tem de ficar em ASCII");
        var prepare = Encoding.ASCII.GetString(bytes);

        var props = XDocument.Parse(CloneStub(prepare, "Directory.Build.props")).Root!;
        Assert.Equal("Project", props.Name.LocalName);
        var group = props.Elements("PropertyGroup").Single();
        Assert.Equal("'$(MSBuildProjectExtension)' == '.csproj'", group.Attribute("Condition")!.Value);
        Assert.Equal("true", group.Element("DeterministicSourcePaths")!.Value);
        Assert.Null(props.Descendants("TreatWarningsAsErrors").FirstOrDefault());

        var targets = XDocument.Parse(CloneStub(prepare, "Directory.Build.targets")).Root!;
        var definitions = targets.Elements("ItemDefinitionGroup").Single();
        Assert.Equal("'$(MSBuildProjectExtension)' == '.vcxproj'", definitions.Attribute("Condition")!.Value);
        Assert.Equal("%(AdditionalOptions) /PDBALTPATH:%_PDB%", definitions.Element("Link")!.Element("AdditionalOptions")!.Value);

        Assert.Contains("if (Test-Path -LiteralPath $f) { throw \"o CefSharp ja tem $stub\" }", prepare);
    }

    // O build-all.ps1 confere as peças nativas logo depois de as ter (também com -SkipCefSharp/-SkipClient), antes dos
    // testes e da marca deles, com a mesma procura do publish.ps1.
    [Fact]
    public void BuildAll_ChecksTheNativePiecesBeforeTheTests()
    {
        var script = Read("build", "sandbox", "build-all.ps1");
        Assert.Contains(". (Join-Path $PSScriptRoot '..\\profile-paths.ps1')", script);
        var check = script.IndexOf("Step 'Sem o caminho de quem compila nas peças nativas'", StringComparison.Ordinal);
        Assert.True(check > script.IndexOf("Fail 'a DLL cliente não passou a verificação (verify-client.ps1)'", StringComparison.Ordinal));
        Assert.True(check < script.IndexOf("if ($SkipTests) {", StringComparison.Ordinal));
        Assert.True(check < script.IndexOf("Run $log 'dotnet' @('test'", StringComparison.Ordinal));
        var block = script[check..script.IndexOf("if ($SkipTests) {", StringComparison.Ordinal)];
        Assert.Contains("$profileScan = New-ProfileScan", block);
        Assert.Contains("Test-ProfilePath $profileScan ([IO.File]::ReadAllBytes($clientDll))", block);
        Assert.Contains("cefsharp.common.netcore.$Official-eteris.$N.nupkg", block);
        Assert.Contains("'runtimes/win-x64/lib/net6.0/CefSharp.Core.dll', 'runtimes/win-x64/lib/net6.0/CefSharp.Core.Runtime.dll'", block);
        Assert.Contains("Test-ProfilePath $profileScan $bytes", block);
        Assert.Contains("Fail (", block);
        // Fora dos ramos -SkipCefSharp/-SkipClient: o bloco não tem «if ($Skip…».
        Assert.DoesNotContain("$SkipCefSharp", block);
        Assert.DoesNotContain("$SkipClient", block);
    }

    // O publish.ps1 usa a mesma procura, nos dois modos, depois da forma publicada e antes da marca do fim.
    [Fact]
    public void Publish_UsesTheSharedProfileScan()
    {
        var publish = Read("build", "publish.ps1");
        var load = publish.IndexOf(". (Join-Path $PSScriptRoot \"profile-paths.ps1\")", StringComparison.Ordinal);
        Assert.True(load > 0, "publish.ps1 sem o dot-source do profile-paths.ps1");
        var test = publish.IndexOf("Test-ProfilePath $profileScan ([IO.File]::ReadAllBytes($file.FullName)) -Pdb:($file.Extension -eq '.pdb')", StringComparison.Ordinal);
        Assert.True(test > load);
        Assert.True(test < publish.LastIndexOf("\"eteris-build.json\"", StringComparison.Ordinal));
        // Fora do if ($NoSandbox) { … } else { … }: vale nos dois modos.
        var branch = Regex.Match(publish, @"if \(\$NoSandbox\) \{(?<off>.*?)\} else \{(?<on>.*?)\r?\n\}", RegexOptions.Singleline);
        Assert.True(branch.Success);
        Assert.True(load > branch.Index + branch.Length);
        Assert.Contains("'^(Eteris|eteris_|CefSharp)'", publish);
        // Sem uma segunda cópia da procura.
        Assert.DoesNotContain("$needles", publish);
    }

    [Fact]
    public void ProfilePaths_LooksForEveryForm()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "build", "profile-paths.ps1"));
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "build/profile-paths.ps1 tem acentos: UTF-8 com BOM (PowerShell 5.1)");
        var script = Encoding.UTF8.GetString(bytes);
        foreach (var piece in new[] { "function New-ProfileScan", "function Test-ProfilePath", "$env:USERPROFILE", ".ShortPath", "GetEncoding(28591)",
                                      "[Text.Encoding]::UTF8.GetBytes($form)", "[Text.Encoding]::Unicode.GetBytes($form)", "OrdinalIgnoreCase",
                                      "'MPDB'", "DeflateStream" })
            Assert.Contains(piece, script);
        foreach (var form in new[] { "\"\\$profileParent\\$name\\\"", "\"/$profileParent/$name/\"", "\"\\\\$profileParent\\\\$name\\\\\"" })
            Assert.Contains(form, script);
    }

    // A procura a sério, no Windows PowerShell 5.1 (o do publish.ps1 e do build-all.ps1), com o perfil desta máquina:
    // nas formas e codificações que os compiladores escrevem, e no PDB portátil embutido (comprimido).
    [Fact]
    public async Task ProfilePaths_FindsTheProfileInEveryWayItGetsIntoABinary()
    {
        var profile = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profile.Length == 0) return; // sem perfil não há o que procurar
        var leaf = Path.GetFileName(profile);
        var parent = Path.GetFileName(Path.GetDirectoryName(profile) ?? "");
        byte[] Pe(byte[] inner) => [.. Encoding.ASCII.GetBytes("MZ\0\0PE\0\0"), .. new byte[64], .. inner, .. new byte[64]];
        byte[] Embedded(byte[] pdb)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(pdb);
            return Pe([.. Encoding.ASCII.GetBytes("RSDS"), .. new byte[20], .. Encoding.ASCII.GetBytes("MPDB"), .. BitConverter.GetBytes(pdb.Length), .. output.ToArray()]);
        }
        var name = Encoding.UTF8.GetBytes(leaf);
        byte[] segment = [(byte)name.Length, .. name];
        var cases = new Dictionary<string, (byte[] Bytes, bool Expected)>
        {
            ["a-limpo.dll"] = (Pe(Encoding.ASCII.GetBytes(@"C:\src\navegador\Eteris.App.pdb")), false),
            ["b-utf8.dll"] = (Pe(Encoding.UTF8.GetBytes($@"C:\{parent}\{leaf}\repo\libcef_dll\wrapper\x.cc")), true),
            ["c-utf16.dll"] = (Pe(Encoding.Unicode.GetBytes($@"C:\{parent}\{leaf}\repo\x.pdb")), true),
            ["d-barras.dll"] = (Pe(Encoding.UTF8.GetBytes($"C:/{parent}/{leaf}/repo/x.cc")), true),
            ["e-embutido-json.dll"] = (Embedded(Encoding.UTF8.GetBytes($$$"""{"documents":{"C:\\{{{parent}}}\\{{{leaf}}}\\repo\\*":"https://x/*"}}""")), true),
            ["f-embutido-segmentos.dll"] = (Embedded([.. Encoding.ASCII.GetBytes("BSJB"), .. segment, 4, .. Encoding.ASCII.GetBytes("repo")]), name.Length >= 4),
            ["g-embutido-limpo.dll"] = (Embedded(Encoding.UTF8.GetBytes("""{"documents":{"/_/*":"https://x/*"}}""")), false),
            ["h-mpdb-falso.dll"] = (Pe([.. Encoding.ASCII.GetBytes("MPDB"), .. BitConverter.GetBytes(100), .. Enumerable.Repeat((byte)0xFF, 200)]), false),
            ["i-segmento.pdb"] = (Pe([.. Encoding.ASCII.GetBytes("BSJB"), .. segment]), name.Length >= 4),
            ["j-segmento-fora-de-pdb.dll"] = (Pe([.. segment, 4, .. Encoding.ASCII.GetBytes("repo")]), false),
        };
        var dir = Directory.CreateTempSubdirectory("eteris-profile-paths-");
        try
        {
            foreach (var (file, value) in cases) File.WriteAllBytes(Path.Combine(dir.FullName, file), value.Bytes);
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            start.Environment["ETERIS_PROFILE_PATHS"] = Path.Combine(Root(), "build", "profile-paths.ps1");
            start.Environment["ETERIS_PROFILE_DIR"] = dir.FullName;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                         "$ErrorActionPreference = 'Stop'; . $env:ETERIS_PROFILE_PATHS; $scan = New-ProfileScan; " +
                         "Get-ChildItem -LiteralPath $env:ETERIS_PROFILE_DIR -File | Sort-Object Name | ForEach-Object { " +
                         "'{0}={1}' -f $_.Name, (Test-ProfilePath $scan ([IO.File]::ReadAllBytes($_.FullName)) -Pdb:($_.Extension -eq '.pdb')) }" })
                start.ArgumentList.Add(argument);
            using var shell = Process.Start(start)!;
            var error = shell.StandardError.ReadToEndAsync();
            var output = await shell.StandardOutput.ReadToEndAsync();
            Assert.True(shell.WaitForExit(60_000), "powershell.exe não acabou em 60 s");
            await error;
            // Sem o texto do erro nem a saída inteira: podem trazer o caminho do perfil (o registo do CI é público).
            Assert.True(shell.ExitCode == 0, $"powershell.exe saiu com o código {shell.ExitCode}");
            var results = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(l => l.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            foreach (var (file, value) in cases)
            {
                Assert.True(results.TryGetValue(file, out var found), $"sem resultado para {file}");
                Assert.True(string.Equals(found, value.Expected.ToString(), StringComparison.OrdinalIgnoreCase), $"{file}: esperado {value.Expected}, deu {found}");
            }
        }
        finally { dir.Delete(recursive: true); }
    }
}
