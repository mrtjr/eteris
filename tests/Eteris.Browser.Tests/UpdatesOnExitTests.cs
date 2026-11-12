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
        var apply = code.IndexOf("UpdateService.ApplyPendingAtStartup(args, restartedByUpdater);", StringComparison.Ordinal);
        Assert.True(apply > code.IndexOf("StartupTrace.Mark(\"instancia\");", StringComparison.Ordinal));
        Assert.True(apply < code.IndexOf("CefConfigurator.StartFromMain(App.OnCefReady);", StringComparison.Ordinal));
    }

    // A guarda do Velopack contra o ciclo de reinícios (I1): o VelopackApp.Run só aplicava sem VELOPACK_RESTART e apaga a
    // variável antes de devolver; por isso o Éteris lê-a antes do Run, com o mesmo teste (não vazia nem só espaços).
    [Fact]
    public void Program_ReadsVelopackRestartBeforeRun()
    {
        var code = Source("Program.cs");
        var read = code.IndexOf("var restartedByUpdater = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(\"VELOPACK_RESTART\"));",
            StringComparison.Ordinal);
        Assert.True(read >= 0, "o Program.Main não lê a VELOPACK_RESTART");
        Assert.True(read < code.IndexOf("velopack.Run();", StringComparison.Ordinal), "a VELOPACK_RESTART tem de ser lida antes do Run (que a apaga)");
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)] [InlineData(false, false, false, false, false, false)]
    [InlineData(true, true, false, false, false, false)] [InlineData(true, false, true, false, false, false)]
    // I1: reaberto pelo Update.exe depois de aplicar (VELOPACK_RESTART) não volta a aplicar: seria um ciclo de reinícios.
    [InlineData(true, false, false, true, false, false)]
    // M5: a nova tentativa do sandbox (RetrySwitch) não aplica: a versão nova arrancaria sem sandbox sem razão.
    [InlineData(true, false, false, false, true, false)]
    public void ShouldApplyAtStartup_OnlyAloneAndWithoutALink(bool pending, bool other, bool link, bool restarted, bool sandboxRetry, bool expected)
        => Assert.Equal(expected, UpdateService.ShouldApplyAtStartup(pending, other, link, restarted, sandboxRetry));

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--eteris-no-sandbox", SandboxFallback.RetrySwitch }, true)]
    [InlineData(new[] { "--ETERIS-SANDBOX-RETRY" }, true)]
    public void IsSandboxRetry_ReadsTheRetrySwitch(string[] args, bool expected)
        => Assert.Equal(expected, UpdateService.IsSandboxRetry(args));

    [Fact]
    public void Tweak_E10_IsGone() => Assert.DoesNotContain(PerfTuning.Tweaks, t => t.Id == "e10");

    [Fact]
    public void TestPatch_FindsTheLineWhereTheManagerIsBorn()
    {
        var service = Source(Path.Combine("Updates", "UpdateService.cs"));
        Assert.Single(Regex.Matches(service, Regex.Escape("try { return new UpdateManager(CreateSource()); }")));
        Assert.DoesNotContain("manager = new UpdateManager(", service);
        Assert.Contains("old1 = 'try { return new UpdateManager(CreateSource()); }'",
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
