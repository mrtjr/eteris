using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// Toda a página web passa pelo PrivacyRequestHandler (Escudo, HTTPS primeiro, cabeçalhos de privacidade). Os testes ao
// vivo (tests/Eteris.Browser.E2E.Tests) provam-no numa aba; este teste fixa que os sítios que criam um browser — a aba,
// o Espreitar, os painéis web e o motor da vigia — o levam todos, e que não aparece um quinto sem passar por aqui.
public class PageBrowsersTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    [Fact]
    public void EveryPageBrowser_HasThePrivacyRequestHandler()
    {
        var src = Path.Combine(Root(), "src", "Eteris.Browser");
        var creator = new Regex(@"new (ChromiumWebBrowser|PageBrowser)\(");
        var creators = 0;
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!creator.IsMatch(lines[i])) continue;
                creators++;
                var next = string.Join("\n", lines.Skip(i + 1).Take(40));
                // O motor da vigia recebe o tratador de quem o cria (MainWindow.Watch.cs) e só o embrulha (WatchRunner).
                var expected = relative == Path.Combine("Watching", "WatchRunner.cs")
                    ? "RequestHandler = new AppRedirectGuard(_requestHandler()"
                    : "RequestHandler = new PrivacyRequestHandler(";
                if (!next.Contains(expected, StringComparison.Ordinal))
                    missing.Add($"{relative}:{i + 1}");
            }
        }
        Assert.True(missing.Count == 0, "browser sem o PrivacyRequestHandler: " + string.Join(", ", missing));
        Assert.Equal(4, creators);
        var watch = File.ReadAllText(Path.Combine(src, "MainWindow.Watch.cs"));
        Assert.Contains("new WatchRunner(", watch);
        Assert.Contains("() => new PrivacyRequestHandler(", watch);
    }
}
