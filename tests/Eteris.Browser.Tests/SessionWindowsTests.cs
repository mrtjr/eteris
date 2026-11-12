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

    [Theory]
    [InlineData(false, true, 0, 3, 0)]   // a última janela normal, com «Continuar onde parei»: fica na sessão
    [InlineData(false, true, 1, 3, 3)]   // fica outra janela normal aberta: esta sai da sessão
    [InlineData(false, false, 0, 3, 3)]  // sem «Continuar onde parei»
    [InlineData(true, true, 0, 3, 3)]    // janela privada
    public void LockedAtRisk_OnlyTheLastNormalWindowIsSafe(bool isPrivate, bool restores, int others, int locked, int expected)
        => Assert.Equal(expected, SessionExit.LockedAtRisk(isPrivate, restores, others, locked));

    [Fact]
    public void OtherWindows_ComeBackAfterTheFirstPage()
    {
        Assert.Contains("public MainWindow(string? initialUrl = null, bool isPrivate = false, Data.SessionWindow? restored = null)", Source("MainWindow.xaml.cs"));
        var session = Source("MainWindow.Session.cs");
        Assert.Contains("FirstPaint.App.Then(RestoreOtherWindows);", session);
        Assert.Contains("ShowActivated = false", session);
        Assert.Contains("SessionExit.LockedAtRisk(", Source("MainWindow.ClosedWindows.cs"));
    }

    // Revisão final, achado 1: a varredura do arranque («esquecer ao fechar») conta as abas das janelas que ainda vão
    // voltar, com os seus Espaços — senão apagava os sites delas pouco antes de as repor.
    [Fact]
    public void Addresses_IncludeEveryWorkspace()
    {
        var session = new Data.Session(["https://a.pt/"], 0,
            Spaces: [new Data.SessionSpace("Geral", 0), new Data.SessionSpace("Trabalho", 1, Tabs: new Data.Session(["https://b.pt/"], 0))]);
        Assert.Equal(["https://a.pt/", "https://b.pt/"], Data.SessionStore.Addresses(session));
    }

    [Fact]
    public void StartupSweep_CountsTheWindowsStillToRestore()
        => Assert.Contains("OpenAddresses().Concat(AddressesToRestore())", Source("MainWindow.Forget.cs"));

    // Revisão final, achado 2: com o ecrã dividido, a outra metade à vista também está em uso (decisão D3).
    [Fact]
    public void SwitchingWorkspace_KeepsTheSplitPartnerAwake()
        => Assert.Matches(@"Tabs\.KeepsAwake = tab => [^;]*_split\?\.PaneOf\(tab\) is not null", Source("MainWindow.xaml.cs"));

    // Revisão final, achados 3 e 4: o encerramento do Windows comporta-se como «Atualizar para…» — a sessão já foi
    // gravada inteira, por isso nenhuma janela pede confirmação pelas trancadas (o WPF fecha-a na mesma e saltava a
    // gravação da posição e das notas) nem apaga sites com «esquecer» de janelas que vão voltar.
    [Fact]
    public void WindowsSessionEnd_BehavesLikeTheUpdate()
    {
        Assert.Contains("Updates.UpdateService.RestartRequested || _endingPrivateSession || s_sessionFrozen ? 0 : LockedTabsAtRisk()",
            Source("MainWindow.ClosedWindows.cs"));
        Assert.Contains("|| Updates.UpdateService.RestartRequested || s_sessionFrozen) return;", Source("MainWindow.Forget.cs"));
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
