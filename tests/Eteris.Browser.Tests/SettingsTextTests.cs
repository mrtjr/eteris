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

    // Proposta 3 (arrumações): as páginas internas falam num só registo, formal e sem pronome («Escolha…», «Pode…»),
    // nunca «tu» nem «você». As Novidades (WhatsNewPage) ficam como estão: são o texto da versão, revisto à parte. Os
    // painéis do WPF são do ciclo da proposta 7.
    public static TheoryData<string> InternalPageSources()
    {
        var folder = Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser");
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(folder, "*.html").Concat(Directory.GetFiles(folder, "*Page.cs")).Order(StringComparer.Ordinal))
            if (Path.GetFileName(file) != "WhatsNewPage.cs") data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(InternalPageSources))]
    public void InternalPages_UseTheFormalRegister(string file)
    {
        var text = Page(file);
        var informal = System.Text.RegularExpressions.Regex.Matches(text,
            @"\b(tu|te|teu|tua|teus|tuas|ti|contigo|você|vocês|podes|queres|tens|estás|estiveres|quiseres|concentrares|escolheste)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.True(informal.Count == 0, file + ": " + string.Join(", ", informal.Select(m => "«" + m.Value + "»")));
        foreach (var old in new[] { "Escolhe uma duração", "Mantém esta aba", "Escreve um endereço", "Seleciona Ao vivo", "Seleciona Imagem", "Respira e começa", "Retoma quando" })
            Assert.DoesNotContain(old, text);
    }

    // A tranca das janelas privadas em Privacidade e segurança, logo a seguir a «Esconder o Éteris do Windows Recall e de
    // capturas»: o interruptor e o tempo (escondido com o interruptor desligado), com o texto honesto de PrivateLockText.
    [Fact]
    public void ThePrivateLock_IsInPrivacy()
    {
        var settings = Page("settings.html");
        var capture = settings.IndexOf("data-key=\"hideFromCapture\"", StringComparison.Ordinal);
        var lockAt = settings.IndexOf("data-key=\"privateLock\"", StringComparison.Ordinal);
        var minutes = settings.IndexOf("<div class=\"seg\" data-key=\"privateLockMinutes\">", StringComparison.Ordinal);
        Assert.True(capture > 0 && lockAt > capture && minutes > lockAt, "a tranca vem a seguir a hideFromCapture");
        Assert.True(lockAt < settings.IndexOf("Apagar dados de navegação", StringComparison.Ordinal));
        foreach (var value in new[] { "5", "15", "60", "0" })
            Assert.Contains($"<button data-value=\"{value}\">", settings[minutes..settings.IndexOf("</div>", minutes, StringComparison.Ordinal)]);
        Assert.Contains("id=\"privateLockMinutesItem\"", settings);
        Assert.Contains("<b>" + Eteris.Browser.Tabs.PrivateLockText.SettingsTitle + "</b><span>" + Eteris.Browser.Tabs.PrivateLockText.SettingsDetail + "</span>", settings);
        Assert.Contains("<b>" + Eteris.Browser.Tabs.PrivateLockText.MinutesTitle + "</b><span>" + Eteris.Browser.Tabs.PrivateLockText.MinutesDetail + "</span>", settings);
        Assert.Contains("não uma cifra", settings);
        // O tempo só se vê com o interruptor ligado (no carregamento e ao mudá-lo) e lê o valor atual como os outros.
        Assert.Contains("privateLockMinutes: String(data.privateLockMinutes)", settings);
        Assert.Matches(@"getElementById\('privateLockMinutesItem'\)\.hidden = !", settings);
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "SettingsPage.cs"));
        Assert.Contains("privateLock = settings.PrivateLock", page);
        Assert.Contains("privateLockMinutes = settings.PrivateLockMinutes", page);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
