using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// E10 do plano de desempenho da 0.2.14 (ajuste e10, à espera da decisão D-7): o Velopack deixa de procurar no arranque um
// pacote para aplicar; o Éteris já aplica as atualizações ao sair (UpdateService.ApplyPendingOnExit).
public class UpdatesOnExitTests
{
    [Fact]
    public void Tweak_IsOffByDefaultAndWaitsForTheOwner()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.UpdatesOnExitOnly);
        Assert.False(tweak.OnByDefault);
        Assert.True(tweak.BeforeFirstPaint);
        Assert.Equal("D-7", tweak.Waits);
    }

    // Só com o ajuste, e antes do Run (é aí que o Velopack aplicaria o pacote); os ganchos rápidos do NetworkSandbox
    // continuam todos.
    [Fact]
    public void Program_TurnsOffTheStartupApplyOnlyWithTheTweak()
    {
        var code = Source("Program.cs");
        Assert.Contains("if (PerfTuning.IsOn(PerfTuning.UpdatesOnExitOnly)) velopack = velopack.SetAutoApplyOnStartup(false);", code);
        Assert.True(code.IndexOf("SetAutoApplyOnStartup(false)", StringComparison.Ordinal) < code.IndexOf("velopack.Run();", StringComparison.Ordinal));
        foreach (var hook in new[] { "OnAfterInstallFastCallback", "OnAfterUpdateFastCallback", "OnBeforeUninstallFastCallback" })
            Assert.Contains(hook, code);
        Assert.Single(Regex.Matches(code, @"\.Run\(\)"));
    }

    // O que o ajuste pressupõe: as atualizações aplicam-se ao sair.
    [Fact]
    public void Updates_AreAppliedOnExit()
        => Assert.Contains("UpdateService.ApplyPendingOnExit();", Source("App.xaml.cs"));

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
