using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ClearDataTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 15, 0, 0);

    [Fact]
    public void Parse_ReadsChoicesAndRange()
    {
        var request = ClearDataRequest.Parse("history,cache", "hour", Now)!;
        Assert.True(request.History);
        Assert.False(request.Cookies);
        Assert.True(request.Cache);
        Assert.Equal(Now.AddHours(-1), request.Since);
    }

    [Fact]
    public void Parse_AllTime()
        => Assert.Equal(DateTime.MinValue, ClearDataRequest.Parse("cookies", "all", Now)!.Since);

    [Theory]
    [InlineData("", "all")]
    [InlineData("passwords", "all")]
    [InlineData("history", "forever")]
    public void Parse_RejectsNothingOrUnknown(string what, string range)
        => Assert.Null(ClearDataRequest.Parse(what, range, Now));

    [Fact]
    public void Summary_IsReadable()
        => Assert.Equal("Histórico, cookies e cache apagados",
            new ClearDataRequest(true, true, true, DateTime.MinValue).Summary);

    [Fact]
    public void Report_SaysWhatFailedAndWhatWasCleared()
    {
        var all = new ClearDataRequest(true, true, true, DateTime.MinValue);
        Assert.Equal("Histórico, cookies e cache apagados", all.Report(new ClearOutcome()));
        Assert.Equal("Não foi possível apagar os cookies. Apagado: histórico e cache.", all.Report(new ClearOutcome(CookiesFailed: true)));
        Assert.Equal("Não foi possível apagar os cookies e a cache. Apagado: histórico.", all.Report(new ClearOutcome(CookiesFailed: true, CacheFailed: true)));
        Assert.Equal("Não foi possível apagar os cookies.", new ClearDataRequest(false, true, false, DateTime.MinValue).Report(new ClearOutcome(CookiesFailed: true)));
        Assert.Equal("Não foi possível apagar a cache, os dados de 2 sites e os dados do motor de vídeo. Apagado: cookies.",
            new ClearDataRequest(false, true, true, DateTime.MinValue).Report(new ClearOutcome(CacheFailed: true, StorageFailed: 2, VideoEngineFailed: true)));
        Assert.Equal("Não foi possível apagar os dados de 1 site. Apagado: cookies e palavras-passe e preenchimento automático.",
            new ClearDataRequest(false, true, false, DateTime.MinValue, Vault: true).Report(new ClearOutcome(StorageFailed: 1)));
    }

    [Fact]
    public void Route_AnswersOnlyAfterTheEngineCleared()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        var start = code.IndexOf("private static async Task<string> ClearBrowsingDataAsync(", StringComparison.Ordinal);
        var end = code.IndexOf("private static IResourceHandler BookmarksRoute(", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var body = code[start..end];
        Assert.Contains("await MainWindow.ClearEngineDataAsync(browserId, request", body);
        Assert.Contains("return request.Report(", body);
        Assert.DoesNotContain("GetDevToolsClient", body);
        Assert.DoesNotContain("Task.Run", body);
        Assert.DoesNotContain("catch (Exception) { }", body);
        Assert.DoesNotContain("ClearBrowsingData(request, browser)", code);
        // O cofre continua a pedir primeiro o Windows Hello: recusado, nada se apaga.
        Assert.Contains("if (request.Vault)", code);
        Assert.Contains("Não foi confirmado com o Windows: nada foi apagado.", code);
        Assert.Contains("'A apagar…'", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "settings.html")));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
