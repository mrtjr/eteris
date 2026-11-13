using System.IO;

namespace Eteris.Browser.Tests;

// Textos das boas-vindas, das Definições e da sincronização contra o que o código faz de facto.
public class SettingsTextTests
{
    private static string Page(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", name));

    [Fact]
    public void NoStaleClaims()
    {
        var settings = Page("settings.html");
        Assert.DoesNotContain("safelinks", settings);
        Assert.Contains("(l.facebook.com, google.com/url nas páginas da Google…)", settings);
        var welcome = Page("welcome.html");
        Assert.DoesNotContain("deixa de ver que sites visita", welcome);
        Assert.Contains("O seu fornecedor deixa de ver os nomes que pergunta ao DNS (vale a partir da próxima vez que abrir o Éteris)", welcome);
        Assert.Contains("Nenhum deles cria um perfil seu, segundo as políticas deles.", welcome);
        // Não há pastas de favoritos (Data/BookmarkStore.cs).
        Assert.DoesNotContain("pastas", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Sync", "SyncChoices.cs")).Split('\n').First(l => l.Contains("[\"bookmarks\"]")));
        Assert.DoesNotContain("Os favoritos e as pastas", File.ReadAllText(Path.Combine(RepoRoot(), "docs", "sincronizacao.md")));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
