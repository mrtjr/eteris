using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// A casa das abas fixadas (Tabs/PinnedHome.cs, MainWindow.PinnedHome.cs) ligada à janela: grava-se com a sessão deste
// computador (nunca a de uma janela privada) e nunca sai dele (não há sincronização de abas).
public class PinnedHomeWiringTests
{
    // Sem isto, «Usar este endereço como casa» só ficava gravado na navegação seguinte.
    [Fact]
    public void HomeChanges_SaveTheSession()
        => Assert.Matches(@"private void OnTabPropertyChanged\(object\? sender, PropertyChangedEventArgs e\)\s*\{\s*if \(e\.PropertyName is [^;]*nameof\(BrowserTab\.Home\)\) ScheduleSessionSave\(\);",
            Source("MainWindow.xaml.cs"));

    // A casa de uma aba privada só vive em memória: a gravação sai logo numa privada, a sessão só lê as janelas normais e as
    // janelas fechadas (que levam os metas) não guardam as privadas.
    [Fact]
    public void PrivateWindows_NeverWriteTheHome()
    {
        var session = Source("MainWindow.Session.cs");
        Assert.Matches(@"private void ScheduleSessionSave\(\)\s*\{\s*if \(IsPrivate\) return;", session);
        Assert.Matches(@"private static void SaveSessionNow\(bool now = false\)\s*\{[^}]*\}[^}]*NormalWindows\(\)", session);
        Assert.Contains("if (IsPrivate || others == 0) return;", Source("MainWindow.ClosedWindows.cs"));
    }

    // Não há sincronização de abas no Éteris: a casa (um endereço pode trazer tokens) fica na sessão deste computador.
    [Fact]
    public void TheHome_NeverLeavesThisPc()
    {
        var sync = Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser", "Sync"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Sync"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(sync);
        Assert.All(sync, f => Assert.DoesNotMatch(@"\bHome\b", File.ReadAllText(f)));
        Assert.Contains("As abas abertas NÃO vão", Source(Path.Combine("Sync", "ProfileTransfer.cs")));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative)
    {
        var path = Path.Combine(Root, "src", "Eteris.Browser", relative);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }
}
