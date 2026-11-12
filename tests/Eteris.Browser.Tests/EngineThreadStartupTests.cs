using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// E11 do plano de desempenho da 0.2.14 (ajuste e11, desligado por omissão: regra R-2 e depois do E9): o Prepare e o
// Cef.Initialize correm na thread do motor desde o Main, fora do fio da interface.
public class EngineThreadStartupTests
{
    [Fact]
    public void Tweak_IsOffByDefaultAndComesAfterE9()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.EngineThreadStartup);
        Assert.False(tweak.OnByDefault);
        Assert.True(tweak.BeforeFirstPaint);
        Assert.Equal("E9", tweak.Waits);
    }

    // Só com o ajuste, depois da instância única (um 2.º lançamento nunca arranca o motor) e com as pastas criadas antes
    // (a base de dados abre-se em paralelo no App.OnStartup).
    [Fact]
    public void Program_StartsTheEngineAfterTheSingleInstanceAndTheFolders()
    {
        var code = Source("Program.cs");
        var instance = code.IndexOf("StartupTrace.Mark(\"instancia\");", StringComparison.Ordinal);
        var folders = code.IndexOf("AppPaths.EnsureDirectories();", StringComparison.Ordinal);
        var start = code.IndexOf("CefConfigurator.StartFromMain(App.OnCefReady);", StringComparison.Ordinal);
        Assert.True(instance > 0 && folders > instance && start > folders);
        Assert.Matches(@"if \(PerfTuning\.IsOn\(PerfTuning\.EngineThreadStartup\)\)\s*\{\s*AppPaths\.EnsureDirectories\(\);\s*CefConfigurator\.StartFromMain", code);
    }

    [Fact]
    public void App_DoesNotStartTheEngineTwice()
        => Assert.Contains("if (!CefConfigurator.StartedFromMain) CefConfigurator.InitializeInBackground(CefConfigurator.Prepare(), OnCefReady);", Source("App.xaml.cs"));

    // A limpeza do perfil (no Prepare) continua antes do Cef.Initialize: na mesma thread, em sequência, dentro do mesmo
    // try (uma falha volta a ser lançada no WaitReady).
    [Fact]
    public void EngineThread_PreparesBeforeInitializing()
        => Assert.Matches(@"try\s*\{\s*var settings = prepare\(\);\s*(?://[^\n]*\s*)*if \(!Cef\.Initialize\(settings,", Source(Path.Combine("Infrastructure", "CefConfigurator.cs")));

    // Sem o ajuste, o caminho de sempre: o Prepare no fio da interface, o mesmo resultado entregue à thread.
    [Fact]
    public void WithoutTheTweak_PrepareStaysOnTheUiThread()
        => Assert.Contains("public static void InitializeInBackground(CefSettings settings, Action ready) => InitializeInBackground(() => settings, ready);",
            Source(Path.Combine("Infrastructure", "CefConfigurator.cs")));

    // A marca do critério: quanto o fio da interface esperou pelo motor.
    [Fact]
    public void WaitReady_MarksTheWait()
    {
        var code = Source(Path.Combine("Infrastructure", "CefConfigurator.cs"));
        Assert.Contains("StartupTrace.MarkOnce(\"cef.esperou\", FormattableString.Invariant($\"ms={waited.Elapsed.TotalMilliseconds:F1}\"));", code);
        Assert.Contains("else StartupTrace.MarkOnce(\"cef.esperou\", \"ms=0\");", code);
    }

    // O que o Prepare escreve para a interface e para as threads do motor sai publicado com Volatile.
    [Fact]
    public void SecureDnsFields_ArePublishedWithVolatile()
    {
        var code = Source(Path.Combine("Shields", "PrivacyPreferences.cs"));
        Assert.Contains("Volatile.Write(ref _startedWithSecureDns, value)", code);
        Assert.Contains("Volatile.Write(ref _secureDnsTemplate, value)", code);
        Assert.DoesNotMatch(@"SecureDnsTemplate \{ get; set; \}", code);
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
