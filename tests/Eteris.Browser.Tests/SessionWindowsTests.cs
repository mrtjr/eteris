using System.IO;
using Eteris.Browser.Tabs;

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

    [Fact]
    public void Order_PutsTheOwnerFirst_AndKeepsTheRest()
    {
        Assert.Equal(["dona", "a", "b"], SessionExit.Order(["a", "dona", "b"], w => w == "dona"));
        Assert.Equal(["a", "b"], SessionExit.Order(["a", "b"], _ => false));
        Assert.Equal(TimeSpan.FromSeconds(10), SessionExit.UnfreezeAfter);
    }

    [Fact]
    public void EveryNormalWindow_SavesTheSession()
    {
        var session = Source("MainWindow.Session.cs");
        Assert.Contains("private static DispatcherTimer? s_sessionTimer;", session);
        Assert.DoesNotContain("private DispatcherTimer? _sessionTimer;", session);
        Assert.Contains("if (!IsPrivate) WatchSessionChanges();", session);
        Assert.Contains("SessionFile.TrySaveWindows(", session);
    }

    [Fact]
    public void ExitFreezesTheSession()
    {
        var update = Source("MainWindow.Update.cs");
        var freeze = update.IndexOf("FreezeSessionForExit();", StringComparison.Ordinal);
        Assert.True(freeze > 0 && freeze < update.IndexOf("window.Close()", StringComparison.Ordinal));
        var app = Source("App.xaml.cs");
        Assert.Contains("protected override void OnSessionEnding(SessionEndingCancelEventArgs e)", app);
        Assert.Contains("MainWindow.FreezeSessionForExit();", app);
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
