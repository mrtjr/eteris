using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Guardas de licenciamento: nenhuma dependência entra na distribuição sem o texto da sua licença.
// Se um destes falhar depois de atualizar um pacote ou crate, corra: python build/licenses/collect.py
public class LicenseComplianceTests
{
    private static readonly string Root = FindRoot();

    // Parte do próprio ficheiro de código: os testes podem correr a partir de uma pasta de compilação fora do repositório.
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void Project_IsGplV3()
        => Assert.StartsWith("GNU GENERAL PUBLIC LICENSE", Read("LICENSE").TrimStart());

    // Um alvo como net10.0-windows10.0.19041.0 junta à instalação o Microsoft.Windows.SDK.NET.dll, sob a licença do
    // Windows SDK (obriga a impor termos aos utilizadores e proíbe licenças de código aberto): incompatível com a GPL.
    [Theory]
    [InlineData("src", "Eteris.Browser", "Eteris.Browser.csproj")]
    [InlineData("tests", "Eteris.Browser.Tests", "Eteris.Browser.Tests.csproj")]
    public void Project_DoesNotPullTheWindowsSdkProjection(params string[] path)
    {
        var csproj = Read(path);
        Assert.Contains("<TargetFramework>net10.0-windows</TargetFramework>", csproj);
        Assert.DoesNotContain("Microsoft.Windows.SDK.NET", csproj);
        Assert.DoesNotContain("WindowsSdkPackageVersion", csproj);
    }

    // Cada versão nova do motor traz componentes e créditos do Chromium diferentes: depois de atualizar o CefSharp
    // (o Dependabot abre o pedido), as licenças têm de ser geradas de novo — python build/licenses/collect.py.
    [Fact]
    public void ChromiumCredits_WereGeneratedForTheCurrentEngine()
    {
        var csproj = Read("src", "Eteris.Browser", "Eteris.Browser.csproj");
        var version = Regex.Match(csproj, "<PackageReference Include=\"CefSharp.Wpf.NETCore\" Version=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(version));
        Assert.True(Read("licenses", "INDEX.md").Contains($"| CefSharp {version} |", StringComparison.Ordinal),
            $"licenses/ foi gerado para outra versão do motor; corra: python build/licenses/collect.py (CefSharp {version})");
    }

    // Publicação com sandbox (-p:EterisSandbox=true, docs/sandbox.md): leva o CefSharp do Éteris (modificado, versão
    // <oficial>-eteris.N), o bootstrap da CEF como Eteris.Browser.exe e o libcef_dll_wrapper da CEF na DLL cliente.
    [Fact]
    public void SandboxPublication_ComponentsAreRegistered()
    {
        var csproj = Read("src", "Eteris.Browser", "Eteris.Browser.csproj");
        var official = Regex.Match(csproj, "<PackageReference Include=\"CefSharp.Wpf.NETCore\" Version=\"([^\"]+)\"").Groups[1].Value;
        Assert.Contains($"Version=\"{official}-eteris.$(EterisCefSharpN)\"", csproj);
        var index = Read("licenses", "INDEX.md");
        Assert.Contains($"| CefSharp {official}-eteris.N, modificado (patch do sandbox em build/cefsharp; só nas publicações com sandbox) | BSD-3-Clause | `CefSharp-LICENSE.txt` |", index);
        Assert.Contains("| Bootstrap da CEF (Eteris.Browser.exe nas publicações com sandbox) e libcef_dll_wrapper (na DLL cliente Eteris.Browser.dll) | BSD-3-Clause | `CEF-LICENSE.txt` |", index);
        // O Eteris.Browser.exe com a marca do Éteris mantém o aviso de copyright da CEF nos recursos de versão.
        Assert.Contains("VALUE \"LegalCopyright\", \"$legal\"", Read("build", "sandbox", "brand-bootstrap.ps1"));
    }

    [Fact]
    public void EveryNuGetPackage_HasItsLicenseText()
    {
        var index = Read("licenses", "INDEX.md");
        var csproj = Read("src", "Eteris.Browser", "Eteris.Browser.csproj");
        // Nome com que cada pacote aparece no índice (por omissão, o próprio id).
        var names = new Dictionary<string, string> { ["CefSharp.Wpf.NETCore"] = "CefSharp" };
        foreach (Match package in Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\""))
        {
            var id = package.Groups[1].Value;
            Assert.True(index.Contains(names.GetValueOrDefault(id, id), StringComparison.OrdinalIgnoreCase),
                $"O pacote {id} não tem licença em licenses/INDEX.md");
        }
    }

    [Fact]
    public void EveryRustCrate_HasItsLicenseFiles()
    {
        var lockFile = Read("src", "Eteris.Shields", "Cargo.lock");
        var crates = Regex.Matches(lockFile, "\\[\\[package\\]\\]\\r?\\nname = \"([^\"]+)\"\\r?\\nversion = \"([^\"]+)\"\\r?\\nsource = \"registry");
        Assert.NotEmpty(crates);
        foreach (Match crate in crates)
        {
            var dir = Path.Combine(Root, "licenses", "rust", $"{crate.Groups[1].Value}-{crate.Groups[2].Value}");
            Assert.True(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any(),
                $"O crate {crate.Groups[1].Value} {crate.Groups[2].Value} não tem licença em licenses/rust/");
        }
    }

    // A Public Suffix List (dados MPL-2.0) vai compilada no eteris_shields.dll pelo crate psl e decide o «site» de
    // Triturar e Esquecer ao fechar: o texto da MPL-2.0 e a origem têm de estar registados, para a versão do Cargo.lock.
    [Fact]
    public void PublicSuffixList_HasItsLicense()
    {
        var lockFile = Read("src", "Eteris.Shields", "Cargo.lock");
        var version = Regex.Match(lockFile, "name = \"psl\"\r?\nversion = \"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(version));
        Assert.Contains($"| Public Suffix List (dados, no crate psl {version} dentro do eteris_shields.dll) | MPL-2.0 | `PublicSuffixList-MPL-2.0.txt` |",
            Read("licenses", "INDEX.md"));
        Assert.StartsWith("Mozilla Public License Version 2.0", Read("licenses", "PublicSuffixList-MPL-2.0.txt").TrimStart());
        Assert.Contains("https://publicsuffix.org", Read("THIRD-PARTY-NOTICES.md"));
        Assert.Contains("psl = ", Read("src", "Eteris.Shields", "Cargo.toml"));
    }

    [Fact]
    public void IndexPointsOnlyToFilesThatExist()
    {
        foreach (Match file in Regex.Matches(Read("licenses", "INDEX.md"), "`([^`]+)` \\|"))
        {
            var path = Path.Combine(Root, "licenses", file.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path) || Directory.Exists(path), $"Falta {file.Groups[1].Value}");
        }
    }

    [Fact]
    public void VendoredLibraries_HaveTheirLicenses()
    {
        var index = Read("licenses", "INDEX.md");
        Assert.Contains("Readability", index);
        Assert.True(File.Exists(Path.Combine(Root, "licenses", "Apache-2.0.txt")));
    }

    // Ficheiros de média que o csproj embute (fundos da nova aba, e o que vier a seguir) também se distribuem: cada um
    // tem de estar na ficha de origem Browser/Wallpapers/README.md, e essa ficha tem de chegar a licenses/INDEX.md.
    // Os globs do csproj expandem-se de propósito: um JPEG esquecido na pasta entraria no executável sem ninguém reparar.
    private static readonly string[] MediaExtensions = [".jpg", ".jpeg", ".png", ".webm", ".webp", ".mp4", ".woff", ".woff2", ".ttf", ".otf", ".svg"];

    [Fact]
    public void EmbeddedMedia_IsListedInTheNotices()
    {
        var project = Path.Combine(Root, "src", "Eteris.Browser");
        var csproj = Read("src", "Eteris.Browser", "Eteris.Browser.csproj");
        var notice = Read("src", "Eteris.Browser", "Browser", "Wallpapers", "README.md");
        var media = new List<string>();
        // Todos os itens que podem ir para o executável ou para a instalação: listas separadas por «;» e globs «**» incluídos.
        foreach (Match item in Regex.Matches(csproj, "<(?:EmbeddedResource|None|Resource|Content) Include=\"([^\"$]+)\""))
        foreach (var include in item.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = include.Replace('\\', Path.DirectorySeparatorChar);
            var deep = pattern.IndexOf("**", StringComparison.Ordinal);
            var dir = Path.Combine(project, deep >= 0 ? pattern[..deep] : Path.GetDirectoryName(pattern) ?? string.Empty);
            if (!Directory.Exists(dir)) continue;
            media.AddRange(Directory.GetFiles(dir, deep >= 0 ? "*" : Path.GetFileName(pattern), deep >= 0 ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
        }
        Assert.Contains(media, f => f.EndsWith("coast.jpg", StringComparison.Ordinal));
        foreach (var file in media)
            Assert.True(notice.Contains(Path.GetFileName(file), StringComparison.Ordinal),
                $"{Path.GetRelativePath(project, file)} entra na distribuição sem origem registada em Browser/Wallpapers/README.md");

        var index = Read("licenses", "INDEX.md");
        Assert.Contains("| Fundos da nova aba (imagens geradas por IA) |", index);
        Assert.Contains("`Wallpapers-NOTICE.txt`", index);
        Assert.Contains("Wallpapers-NOTICE.txt", Read("THIRD-PARTY-NOTICES.md"));

        // A ficha distribuída é a cópia do README feita pelo collect.py: se divergirem, o README mudou sem o correr.
        static string Normalise(string text) => text.Replace("\r\n", "\n").Trim();
        Assert.True(Normalise(notice) == Normalise(Read("licenses", "Wallpapers-NOTICE.txt")),
            "licenses/Wallpapers-NOTICE.txt difere de Browser/Wallpapers/README.md: corra python build/licenses/collect.py");
    }

    [Fact]
    public void ChromiumCredits_AreTheGeneratedPage()
        => Assert.StartsWith("<!-- Generated by licenses.py", File.ReadLines(Path.Combine(Root, "licenses", "Chromium-credits.html")).First());

    // Motor com codecs da plataforma (build/cef-codecs, docs/motor.md): com a compilação própria presente, licenses/ tem
    // de ter sido gerado a partir dela — a licença e os créditos dela, o FFmpeg (LGPL) e a oferta do código-fonte. Sem
    // ela (por exemplo no CI), qualquer das duas gerações serve; o aviso em THIRD-PARTY-NOTICES.md está sempre.
    [Fact]
    public void PlatformCodecs_LicensesFollowTheEngineThatShips()
    {
        var index = Read("licenses", "INDEX.md");
        var runtime = Path.Combine(Root, "build", "cef-codecs", "out", "runtime");
        var ownRuntime = File.Exists(Path.Combine(runtime, "libcef.dll")) && File.Exists(Path.Combine(runtime, "manifest.json"));
        var ownLicenses = index.Contains("| Chromium Embedded Framework 156.0.15 (compilação própria, codecs do Windows) | BSD-3-Clause | `CEF-LICENSE.txt` |", StringComparison.Ordinal);
        if (ownRuntime)
        {
            Assert.True(ownLicenses, "A compilação própria da CEF está em build/cef-codecs/out/runtime: corra python build/licenses/collect.py");
            Assert.Equal(File.ReadAllText(Path.Combine(runtime, "licenses", "LICENSE.txt")), Read("licenses", "CEF-LICENSE.txt"));
            Assert.True(File.ReadAllBytes(Path.Combine(runtime, "licenses", "CREDITS.html")).SequenceEqual(
                File.ReadAllBytes(Path.Combine(Root, "licenses", "Chromium-credits.html"))), "Chromium-credits.html não é o CREDITS.html da compilação própria");
        }
        if (ownLicenses)
        {
            Assert.Contains("| LGPL-2.1-or-later | `FFmpeg-LICENSE.txt` |", index);
            Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", Read("licenses", "FFmpeg-LICENSE.txt"));
            Assert.Contains("Version 2.1", Read("licenses", "FFmpeg-LICENSE.txt"));
            var offer = Read("licenses", "FFmpeg-SOURCE.txt");
            Assert.Contains("cf671c072925b27823a61181152f4f288e8e4306", offer);
            Assert.Contains("chromium/third_party/ffmpeg/+/", offer);
            Assert.Contains("platform-codecs.patch", offer);
            Assert.Contains("ffmpeg-adts-demuxer.patch", offer);
            Assert.Contains("três anos", offer);
            Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", Read("licenses", "Chromium-credits.html"));
        }
        else
        {
            Assert.Contains("| Chromium Embedded Framework | BSD-3-Clause | `CEF-LICENSE.txt` |", index);
        }
        var notices = Read("THIRD-PARTY-NOTICES.md");
        Assert.Contains("Nenhum descodificador com patentes vai incluído", notices);
        Assert.Contains("build/cef-codecs", notices);
        Assert.Contains("licenses/FFmpeg-SOURCE.txt", notices);
    }

    [Fact]
    public void LicensesShipWithTheApp()
    {
        var csproj = Read("src", "Eteris.Browser", "Eteris.Browser.csproj");
        Assert.Contains("Include=\"..\\..\\LICENSE\"", csproj);
        Assert.Contains("Include=\"..\\..\\licenses\\**\\*\" LinkBase=\"licenses\"", csproj);
    }
}
