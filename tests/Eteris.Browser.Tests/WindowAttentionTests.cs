using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// Chamar a atenção para a janela do Éteris (piscar na barra de tarefas e tentar vir para a frente): quando uma
// transferência acaba fora de vista e quando a sessão Google fica iniciada no navegador do sistema.
public class WindowAttentionTests
{
    [Fact]
    public void ShouldAsk_OnlyWhenTheWindowIsNotActive()
    {
        Assert.True(WindowAttention.ShouldAsk(windowActive: false));
        Assert.False(WindowAttention.ShouldAsk(windowActive: true));
    }

    [Fact]
    public void Flash_LivesInInfrastructure_NotInTheDownloadsFile()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var downloads = File.ReadAllText(Path.Combine(src, "MainWindow.Downloads.cs"));
        Assert.Contains("WindowAttention.Flash(", downloads);
        Assert.DoesNotContain("DllImport", downloads);
        Assert.DoesNotContain("FLASHWINFO", downloads);
        // O piscar das transferências continua com a mesma regra.
        Assert.Contains("if (TaskbarProgress.ShouldFlash(_hadActiveDownloads, active, IsActive)) FlashTaskbar();", downloads);

        Assert.Contains("SignedIn = MainWindow.SignInArrived", File.ReadAllText(Path.Combine(src, "Sync", "GoogleSync.cs")));
        Assert.DoesNotContain("SignInArrived", File.ReadAllText(Path.Combine(src, "MainWindow.xaml.cs")));
        Assert.Contains("internal static void SignInArrived()", File.ReadAllText(Path.Combine(src, "MainWindow.Sync.cs")));

        var attention = File.ReadAllText(Path.Combine(src, "Infrastructure", "WindowAttention.cs"));
        Assert.Contains("FlashWindowEx", attention);
        Assert.Contains("SetForegroundWindow", attention);
        Assert.Contains("0x2 | 0xC", attention); // FLASHW_TRAY | FLASHW_TIMERNOFG
        // Só piscar e tentar o primeiro plano: nunca restaurar nem maximizar.
        Assert.DoesNotContain("ShowWindow", attention);
    }
}
