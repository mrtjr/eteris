using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// PRO-15 (ARQ-14): o README diz o que é preciso para compilar e testar, as ligações apontam para ficheiros que existem,
// e o desenho inicial (2026-11-05), com partes abandonadas, está marcado como histórico.
public class ReadmeTests
{
    [Fact]
    public void Readme_SaysWhatIsNeededToBuildAndTest()
    {
        var readme = Read("README.md").Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = readme.IndexOf("## Desenvolvimento", StringComparison.Ordinal);
        var end = readme.IndexOf("## Licença", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "README.md sem «## Desenvolvimento» antes de «## Licença»");
        var section = readme[start..end];
        foreach (var needed in new[] { ".NET 10", "Rust 1.88","Node 22", "global.json", "tests/Eteris.Browser.E2E.Tests" })
            Assert.True(section.Contains(needed, StringComparison.Ordinal), $"«## Desenvolvimento» não diz «{needed}»");
    }

    [Fact]
    public void Readme_LinksPointToExistingFiles()
    {
        var missing = Regex.Matches(Read("README.md"), @"\]\(([^)]+)\)")
            .Select(m => m.Groups[1].Value)
            .Where(target => !target.StartsWith("http", StringComparison.Ordinal) && !target.StartsWith('#'))
            .Select(target => target.Split('#')[0])
            .Where(target => !File.Exists(Path.Combine(Root(), target)) && !Directory.Exists(Path.Combine(Root(), target)))
            .ToList();
        Assert.True(missing.Count == 0, "Ligações do README.md para ficheiros que não existem: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("docs/superpowers/specs/2026-11-05-eteris-browser-design.md")]
    [InlineData("docs/superpowers/plans/2026-11-05-fase-1-esqueleto.md")]
    public void OldDesignDocuments_SayTheyAreHistorical(string document)
    {
        var head = string.Join("\n", Read(document).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(5));
        Assert.Contains("Documento histórico (2026-11-05)", head);
    }

    [Fact]
    public void SearchEngine_HasNoEngineThatWasNeverReleased()
        => Assert.DoesNotContain("atenix.duckdns.org", Read("src/Eteris.Browser/Omnibox/SearchEngine.cs"));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
