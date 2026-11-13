using System.IO;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Tests;

// DAD-14, DES-02: um DispatcherTimer ligado fica na lista do Dispatcher e segura a janela fechada (e tudo o que ela tem)
// até o Éteris sair. O do sono das abas nunca parava; a regra obriga cada temporizador novo da janela a dizer como pára.
public class WindowTimerRulesTests
{
    // Temporizadores periódicos da janela: cada um pára no Closed, com este texto.
    private static readonly Dictionary<string, string> StoppedOnClose = new()
    {
        ["_archiveTimer"] = "Closed += (_, _) => _archiveTimer.Stop();",
        ["_audioTimer"] = "Closed += (_, _) => _audioTimer.Stop();",
        ["_autoHideTimer"] = "Closed += (_, _) => StopAutoHideTimer();",
        ["_obsTimer"] = "Closed += (_, _) => StopObservatoryTimer();",
        ["_snoozeTimer"] = "Closed += (_, _) => _snoozeTimer.Stop();",
        ["_cloakTimer"] = "_cloakTimer?.Stop();",
        ["_guestSeen"] = "Closed += (_, _) => _guestSeen?.Stop();",
        ["_pairingTick"] = "Closed += (_, _) => _pairingTick?.Stop();",
        ["_sleepTimer"] = "Closed += (_, _) => _sleepTimer.Stop();",
        // O relógio da vigia pára no Closed, com o lote a meio cancelado e o motor largado (MainWindow.Watch.cs).
        ["_watchTimer"] = "_watchTimer.Stop();",
        // A inatividade de uma janela privada (MainWindow.PrivateLock.cs) pára no Closed, com o contador das privadas.
        ["_privateIdleTimer"] = "_privateIdleTimer?.Stop();",
    };

    // De disparo único, ou que param sozinhos quando o trabalho acaba.
    private static readonly string[] SelfStopping =
        ["_glanceReleaseTimer", "_groupPreviewTimer", "_notesSave", "_warmTimer", "_dragScrollTimer", "_signInBarHide"];

    [Fact]
    public void EveryWindowTimer_StopsWhenTheWindowCloses()
    {
        var all = string.Join("\n", Directory.GetFiles(Path.Combine(Root, "src", "Eteris.Browser"), "MainWindow*.cs").Select(File.ReadAllText));
        var fields = Regex.Matches(all, @"private (?:System\.Windows\.Threading\.)?DispatcherTimer\? (_\w+);").Select(m => m.Groups[1].Value).Order().ToList();
        Assert.Equal(StoppedOnClose.Keys.Concat(SelfStopping).Order(), fields);
        Assert.All(StoppedOnClose, pair => Assert.Contains(pair.Value, all));
        Assert.DoesNotContain("_sleepTimer.Start();", all);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
