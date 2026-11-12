using System.IO;
using System.Text.RegularExpressions;
using Eteris.Sync.Model;

namespace Eteris.Browser.Tests;

// O sítio público e os textos do gestor de palavras-passe contra o que a sincronização leva de facto (SyncGroups).
public class PublicSiteSyncTests
{
    // Palavra de cada linha de SyncGroups ligada por omissão, em português e em inglês, como o sítio as escreve.
    private static readonly Dictionary<string, (string Pt, string En)> Words = new()
    {
        ["bookmarks"] = ("Favoritos", "Bookmarks"), ["spaces"] = ("Espaços", "Spaces"), ["settings"] = ("definições", "settings"),
        ["shield"] = ("decisões do Escudo", "Shield decisions"), ["notes"] = ("notas", "notes"), ["passwords"] = ("palavras-passe", "passwords"),
        ["contacts"] = ("contactos", "contact details"), ["travel"] = ("viagens", "trips"),
    };

    [Theory]
    [InlineData("index.html")]
    [InlineData("privacidade.html")]
    public void PublicSite_NamesEverySyncGroupOnByDefault(string page)
    {
        var html = Flat(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "site", page)));
        var at = html.IndexOf("id=\"en\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        foreach (var group in SyncGroups.All.Where(g => g.DefaultOn))
        {
            Assert.True(Words.TryGetValue(group.Id, out var word), $"linha nova de SyncGroups sem palavra no sítio: {group.Id}");
            Assert.Contains(word.Pt, html[..at]);
            Assert.Contains(word.En, html[at..]);
        }
    }

    [Fact]
    public void PasswordManagerTexts_ExceptTheEncryptedSyncCopy()
    {
        string Read(params string[] parts) => Flat(File.ReadAllText(Path.Combine([RepoRoot(), .. parts])));
        Assert.Contains("As palavras-passe ficam cifradas neste computador; para a Google só vai a cópia cifrada, e só se ligar a sincronização.",
            Read("src", "Eteris.Browser", "Browser", "settings.html"));
        foreach (var text in new[] { Read("src", "Eteris.Browser", "Browser", "settings.html"), Read("README.md"), Read("docs", "paridade.md") })
            Assert.DoesNotContain("nada vai para a Google", text);
    }

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
