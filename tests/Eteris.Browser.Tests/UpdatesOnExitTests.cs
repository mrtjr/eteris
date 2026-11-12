using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Updates;

namespace Eteris.Browser.Tests;

// INF-03 (auditoria 2): o Velopack nunca aplica um pacote descarregado no arranque (corria antes da instância única e
// terminava o Éteris aberto); o Éteris aplica-o ao sair (UpdateService.ApplyPendingOnExit) ou, depois de ser a instância
// principal, sozinho e sem link nos argumentos (UpdateService.ApplyPendingAtStartup). Regra fixa: o ajuste e10 saiu.
public class UpdatesOnExitTests
{
    // Sem condição e antes do Run (é aí que o Velopack aplicaria o pacote); os ganchos rápidos do NetworkSandbox
    // continuam todos; a aplicação no arranque fica depois da instância única e antes do motor.
    [Fact]
    public void Program_NeverLetsVelopackApplyOnStartup()
    {
        var code = Source("Program.cs");
        Assert.Contains(".SetAutoApplyOnStartup(false)", code);
        Assert.DoesNotContain("UpdatesOnExitOnly", code);
        Assert.True(code.IndexOf(".SetAutoApplyOnStartup(false)", StringComparison.Ordinal) < code.IndexOf("velopack.Run();", StringComparison.Ordinal));
        foreach (var hook in new[] { "OnAfterInstallFastCallback", "OnAfterUpdateFastCallback", "OnBeforeUninstallFastCallback" }) Assert.Contains(hook, code);
        Assert.Single(Regex.Matches(code, @"velopack\.Run\(\)"));
        var apply = code.IndexOf("UpdateService.ApplyPendingAtStartup(args);", StringComparison.Ordinal);
        Assert.True(apply > code.IndexOf("StartupTrace.Mark(\"instancia\");", StringComparison.Ordinal));
        Assert.True(apply < code.IndexOf("CefConfigurator.StartFromMain(App.OnCefReady);", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false, false, true)] [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)] [InlineData(true, false, true, false)]
    public void ShouldApplyAtStartup_OnlyAloneAndWithoutALink(bool pending, bool other, bool link, bool expected)
        => Assert.Equal(expected, UpdateService.ShouldApplyAtStartup(pending, other, link));

    [Fact]
    public void Tweak_E10_IsGone() => Assert.DoesNotContain(PerfTuning.Tweaks, t => t.Id == "e10");

    [Fact]
    public void TestPatch_FindsTheLineWhereTheManagerIsBorn()
    {
        var service = Source(Path.Combine("Updates", "UpdateService.cs"));
        Assert.Single(Regex.Matches(service, Regex.Escape("try { return new UpdateManager(new GithubSource(RepoUrl, null, false)); }")));
        Assert.DoesNotContain("manager = new UpdateManager(", service);
        Assert.Contains("old1 = 'try { return new UpdateManager(new GithubSource(RepoUrl, null, false)); }'",
            File.ReadAllText(Path.Combine(Root, "build", "sandbox", "provas", "patch-velopack-teste.py")));
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
