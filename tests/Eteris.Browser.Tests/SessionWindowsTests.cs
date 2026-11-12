using System.IO;

namespace Eteris.Browser.Tests;

// Auditoria 4 (ARQ-02, DAD-13, DAD-02, ARQ-06): a sessão grava-se sem nunca lançar e leva todas as janelas normais.
// O que só existe na janela (MainWindow) fica fixado por texto sobre o código; nenhum teste cria uma MainWindow.
public sealed class SessionWindowsTests
{
    [Fact]
    public void SaveSession_NeverThrows()
    {
        var session = Source("MainWindow.Session.cs");
        Assert.Contains("TrySave", session);
        Assert.DoesNotContain("catch (IOException) { }", session);
        Assert.Contains("Não foi possível guardar a sessão", session);
        Assert.Contains("Loaded += (_, _) => ShowProfileNotices();", session);
        // Com ação o aviso fica 6 s e não foge com o rato por cima (ToastPolicy.Hold).
        Assert.Contains("new ToastAction(\"Entendi\"", Source("MainWindow.Toast.cs"));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
