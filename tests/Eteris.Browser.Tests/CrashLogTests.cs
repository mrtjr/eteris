using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// INF-08, UIX-15, ARQ-03: um erro inesperado deixa um registo local (só o tipo e a pilha; a mensagem pode trazer endereços e
// caminhos, também de janelas privadas), a janela aberta recupera, e uma falha do motor ao arrancar diz a causa em vez de
// fechar em silêncio.
public sealed class CrashLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eteris-falhas-").FullName;
    private string File_ => Path.Combine(_dir, CrashLog.FileName);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Format_HasTypeAndStack_ButNeverTheMessage()
    {
        Exception caught;
        try { throw new InvalidOperationException("https://segredo.example/?q=1 pasta-da-ana"); } catch (Exception ex) { caught = ex; }
        var entry = CrashLog.Format(new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc), "0.2.15", "interface", caught);
        Assert.StartsWith("## 2026-11-11T10:00:00Z 0.2.15 interface System.InvalidOperationException", entry);
        Assert.Contains(nameof(Format_HasTypeAndStack_ButNeverTheMessage), entry);
        Assert.DoesNotContain("segredo", entry);
        Assert.DoesNotContain("pasta-da-ana", entry);
    }

    [Fact]
    public void Format_ListsInnerExceptionTypes()
    {
        var entry = CrashLog.Format(DateTime.UtcNow, "0.2.15", "tarefa", new Exception("a segredo", new FormatException("b segredo")));
        Assert.Contains("-> System.FormatException", entry);
        Assert.DoesNotContain("segredo", entry);
    }

    [Fact]
    public void Format_ListsEveryExceptionOfAnAggregate()
    {
        var entry = CrashLog.Format(DateTime.UtcNow, "0.2.15", "tarefa",
            new AggregateException("x segredo", new TimeoutException("y segredo"), new ArgumentException("z segredo")));
        Assert.Contains("-> System.TimeoutException", entry);
        Assert.Contains("-> System.ArgumentException", entry);
        Assert.DoesNotContain("segredo", entry);
    }

    [Fact]
    public void Write_KeepsTheLast20()
    {
        for (var i = 1; i <= 25; i++) CrashLog.Write(File_, $"## e{i:00}\n pilha");
        Assert.Equal(20, CrashLog.Count(File_));
        var text = File.ReadAllText(File_);
        Assert.Contains("## e25", text);
        Assert.Contains("## e06", text);
        Assert.DoesNotContain("## e05", text);
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public void Write_NeverThrows_OnAnUnwritablePath()
    {
        File.WriteAllText(File_, "");                       // «File_» é um ficheiro: por baixo dele não se pode escrever
        var under = Path.Combine(File_, "x.log");
        CrashLog.Write(under, "## e");
        Assert.Equal(0, CrashLog.Count(under));
    }

    [Fact]
    public void Write_CreatesTheFolderAndSurvivesAnUnreadableLog()
    {
        var file = Path.Combine(_dir, "nova", CrashLog.FileName);
        CrashLog.Write(file, "## e1\n pilha");
        Assert.Equal(1, CrashLog.Count(file));
        File.WriteAllBytes(file, [0xFF, 0xFE, 0x00, 0x01]);  // lixo: não é um registo, não se perde a entrada nova
        CrashLog.Write(file, "## e2\n pilha");
        Assert.Contains("## e2", File.ReadAllText(file));
    }

    [Fact]
    public void Count_OfAMissingFile_IsZero() => Assert.Equal(0, CrashLog.Count(File_));

    [Fact]
    public void ShouldNotify_AtMostOncePerMinute()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(CrashLog.ShouldNotify(null, t));
        Assert.False(CrashLog.ShouldNotify(t, t.AddSeconds(59)));
        Assert.True(CrashLog.ShouldNotify(t, t.AddSeconds(60)));
    }

    [Fact]
    public void StartFailureText_NamesTheCause_WithoutTheExceptionMessage()
    {
        Assert.Contains("266", CefConfigurator.StartFailureText(new EngineStartException(266)));
        var text = CefConfigurator.StartFailureText(new UnauthorizedAccessException("C:\\segredo"));
        Assert.Contains("pasta do perfil", text);
        Assert.DoesNotContain("segredo", text);
        Assert.Contains(CrashLog.FileName, text);
        var other = CefConfigurator.StartFailureText(new InvalidOperationException("segredo"));
        Assert.Contains("InvalidOperationException", other);
        Assert.DoesNotContain("segredo", other);
        Assert.EndsWith("Os detalhes ficaram em falhas.log, na pasta do perfil.", other);
    }

    [Fact]
    public void EngineStartException_KnowsTheNormalExit()
    {
        Assert.True(new EngineStartException((int)CefSharp.Enums.ResultCode.NormalExitProcessNotified).IsNormalExit);
        Assert.False(new EngineStartException(266).IsNormalExit);
        Assert.Equal(266, new EngineStartException(266).ExitCode);
    }

    [Fact]
    public void App_InstallsTheHandlersBeforeTheWindow_AndCatchesAStartFailure()
    {
        var code = GoogleSyncTests.Src("Eteris.Browser", "App.xaml.cs");
        Assert.True(code.IndexOf("CrashLog.Install(", StringComparison.Ordinal) < code.IndexOf("new MainWindow(", StringComparison.Ordinal));
        Assert.Contains("CefConfigurator.StartFailureText(", code);
        // Um arranque falhado não passa pelo OnExit, que o daria por bom (StartupGuard.Healthy, StartupRescue.Healthy).
        Assert.Contains("Environment.Exit(1);", code);
        Assert.DoesNotContain("Shutdown(1)", code);
        var log = GoogleSyncTests.Src("Eteris.Browser", "Infrastructure", "CrashLog.cs");
        foreach (var handler in new[] { "DispatcherUnhandledException", "AppDomain.CurrentDomain.UnhandledException", "TaskScheduler.UnobservedTaskException" })
            Assert.Contains(handler, log);
        Assert.DoesNotContain(".Message", log);             // nunca a mensagem da exceção
        Assert.DoesNotContain("ex.ToString()", log);
    }

    [Fact]
    public void CefInitialize_ThatFails_ThrowsAnEngineStartException()
    {
        var code = GoogleSyncTests.Src("Eteris.Browser", "Infrastructure", "CefConfigurator.cs");
        Assert.Contains("if (!Cef.Initialize(settings, performDependencyCheck: _dependencyCheck, browserProcessHandler: null)) throw new EngineStartException((int)Cef.GetExitCode());", code);
    }

    [Fact]
    public void SettingsAbout_ShowsTheCrashLog_OnlyWhenThereAreCrashes()
    {
        var withNone = SettingsPageJson(new Browser.SettingsPage.About("1", "2", true, [], "", false));
        Assert.Null(withNone.GetProperty("crashLog").GetString());
        var many = SettingsPageJson(new Browser.SettingsPage.About("1", "2", true, [], "", false, Crashes: 3));
        var line = many.GetProperty("crashLog").GetString()!;
        Assert.StartsWith("3 falhas registadas, só com o tipo e a pilha: ", line);
        Assert.EndsWith(CrashLog.FileName, line);
        Assert.Contains("crashLog", GoogleSyncTests.Src("Eteris.Browser", "Browser", "settings.html"));
        Assert.Contains("CrashLog.Count(", GoogleSyncTests.Src("Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
    }

    [Fact]
    public void SettingsAbout_OneCrash_IsSingular()
    {
        var one = SettingsPageJson(new Browser.SettingsPage.About("1", "2", true, [], "", false, Crashes: 1));
        Assert.StartsWith("1 falha registada, ", one.GetProperty("crashLog").GetString());
    }

    private static System.Text.Json.JsonElement SettingsPageJson(Browser.SettingsPage.About about)
    {
        var html = Browser.SettingsPage.Render(new Data.UserSettings(), about);
        const string open = "<script type=\"application/json\" id=\"data\">";
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return System.Text.Json.JsonDocument.Parse(html[start..end]).RootElement;
    }
}
