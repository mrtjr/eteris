using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;

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

    // O motor não deixou ler os cookies: os sites que só se conheciam por eles ficam com o armazenamento, e di-lo.
    [Fact]
    public void Report_SaysWhenTheCookieSitesWereUnknown()
    {
        Assert.Equal("Não foi possível apagar o armazenamento dos sites que só tinham cookies. Apagado: cookies.",
            new ClearDataRequest(false, true, false, DateTime.MinValue).Report(new ClearOutcome(CookieSitesUnknown: true)));
        Assert.True(new ClearOutcome(CookieSitesUnknown: true).Failed);
    }

    [Fact]
    public void ClearEngineData_MarksTheCookieSitesUnknown_WhenTheEngineDidNotListThem()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.ClearData.cs"));
        Assert.Contains("catch (Exception) { domains = []; cookieSitesUnknown = true; }", code);
        Assert.Contains("CookieSitesUnknown = cookieSitesUnknown", code);
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

    // Uma exceção a meio (contexto privado já libertado, Dispatcher abortado, a confirmação do Windows) não pode dar o
    // texto genérico do VaultResourceHandler, que a página mostraria com ✓.
    [Fact]
    public async Task Reply_SaysNotCleared_WhenTheClearingThrows()
    {
        Assert.Equal("Não foi possível apagar os dados de navegação.", await ClearDataRequest.ReplyAsync(() => throw new ObjectDisposedException("contexto")));
        Assert.Equal("Cookies apagados", await ClearDataRequest.ReplyAsync(() => Task.FromResult("Cookies apagados")));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        var start = code.IndexOf("private IResourceHandler ClearData(", StringComparison.Ordinal);
        var end = code.IndexOf("private static async Task<string> ClearBrowsingDataAsync(", StringComparison.Ordinal);
        var route = code[start..end];
        Assert.Equal(2, route.Split("new VaultResourceHandler(async () => Vault.VaultReply.Text(await ClearDataRequest.ReplyAsync(").Length - 1);
    }

    [Fact]
    public void Cookies_ClearStorageOfEveryKnownOrigin()
    {
        var origins = SiteScope.KnownOrigins(
            ["https://a.pt/x", "https://a.pt:443/y", "http://b.pt:8080/z", "eteris://settings/"],
            [".c.pt", "www.c.pt"],
            ["https://d.pt/aberta", "eteris://newtab/"]);
        Assert.Equal(["http://b.pt:8080", "http://c.pt", "http://www.c.pt", "https://a.pt", "https://c.pt", "https://d.pt", "https://www.c.pt"], origins);
        Assert.Null(SiteScope.OriginOf("eteris://settings/"));
        Assert.Equal("https://a.pt", SiteScope.OriginOf("https://A.pt/x?y"));
    }

    [Fact]
    public void SettingsPage_SaysCookiesSiteDataAndCacheGoEntirely()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "settings.html"));
        Assert.Contains("Cookies e dados de sites <small>(termina sessões; apaga todos os cookies e o que guardaram os sites do histórico e das abas abertas, seja qual for o período)</small>", settings);
        // Não promete «tudo»: o armazenamento de um site que já não está no histórico, nos cookies nem numa aba fica.
        Assert.DoesNotContain("apaga tudo, desde sempre", settings);
        Assert.Contains("Imagens e ficheiros em cache <small>(apaga tudo, seja qual for o período)</small>", settings);
    }

    // Numa janela privada a limpeza é a do contexto dela: não se leem as origens do histórico do perfil.
    [Fact]
    public void PrivateWindow_DoesNotReadTheProfileHistory()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.Contains("request.Cookies && !privateContext ? AppData.TryRead(h => h.Origins()) : []", code);
        Assert.Contains("ClearBrowsingDataAsync(request, browserId, privateContext)", code);
        var window = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.ClearData.cs"));
        Assert.Contains("IsPrivate ? Tabs.Tabs.Select(t => t.Address) : OpenAddresses()", window);
    }

    // Revisão final do plano 6: a janela privada não apaga o histórico do perfil — nem «Apagar dados › histórico» nem
    // «Esquecer os últimos minutos» a partir dela (o histórico do perfil não é dela).
    [Fact]
    public void PrivateWindow_DoesNotForgetTheProfileHistory()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        var clear = code[code.IndexOf("private static async Task<string> ClearBrowsingDataAsync(", StringComparison.Ordinal)..];
        clear = clear[..clear.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("if (request.History && !privateContext)", clear);
        var palette = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Palette.cs"));
        var forget = palette[palette.IndexOf("private void Forget(int minutes)", StringComparison.Ordinal)..];
        forget = forget[..forget.IndexOf("\n    }", StringComparison.Ordinal)];
        var gate = forget.IndexOf("if (!IsPrivate)", StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < forget.IndexOf("AppData.ForgetSince(", StringComparison.Ordinal)
                    && gate < forget.IndexOf("ForgetBounceVisits(", StringComparison.Ordinal));
        Assert.DoesNotContain("em todas as janelas\", null, () => Forget(", palette);
    }

    // A compactação de «Apagar tudo» e a única ao sair correm ao lado da fila do escritor (HistoryWriterTests).
    [Fact]
    public void Compaction_RunsAsideOfTheWriterQueue()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Data", "AppData.cs"));
        Assert.DoesNotContain("QueueWrite(h => h.Compact())", code);
        Assert.Contains("LazyWriter.Value.RunAside(h => h.Compact())", code);
        Assert.Contains("LazyWriter.Value.RunAsideNow(h => h.CompactOnce())", code);
    }

    // Ao sair, a compactação única (pode demorar com um histórico grande) vem depois das limpezas de privacidade: o
    // apagar dos cookies, as palavras-passe por apagar de vez e a área de transferência não esperam por ela.
    [Fact]
    public void OnExit_ClosesTheHistoryAfterThePrivacySteps()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "App.xaml.cs"));
        var close = code.IndexOf("Data.AppData.CloseHistory();", StringComparison.Ordinal);
        Assert.True(close > 0);
        foreach (var step in new[] { "DeleteCookiesAsync(string.Empty, string.Empty)", "Vault.VaultRoutes.CommitPendingOnExit();", "Vault.SecureClipboard.ClearPendingOnExit();" })
            Assert.True(code.IndexOf(step, StringComparison.Ordinal) is var at && at > 0 && at < close, step);
    }

    // O CHANGELOG da 0.2.17 nomeia os armazéns com secure_delete; se um armazém o ganhar ou perder, a frase muda com ele.
    [Fact]
    public void SecureDelete_ChangelogNamesTheStoresThatHaveIt()
    {
        var data = Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Data");
        var stores = Directory.GetFiles(data, "*Store.cs")
            .Where(f => File.ReadAllText(f).Contains("SqliteSetup.SecureDelete(connection);", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["ArchivedTabsStore", "DoneTabsStore", "HistoryStore", "PrivacyEventsStore", "SavedSessionsStore", "SnoozedTabsStore"], stores);
        var log = File.ReadAllText(Path.Combine(RepoRoot(), "CHANGELOG.md"));
        log = log[..log.IndexOf("## 0.2.16", StringComparison.Ordinal)];
        Assert.DoesNotContain("todas as ligações dos armazéns", log);
        Assert.Contains("no histórico, nas «Feitas», no Arquivo, nas «Adiadas», nas sessões guardadas e no relatório de privacidade", log);
        Assert.Contains("os favoritos, a lista «Para ler», a sessão das abas abertas e as notas não estão incluídos", log);
    }

    [Fact]
    public void ForgetCommand_SaysWhatItReaches_InEachKindOfWindow()
    {
        Assert.Contains("em todas as janelas", MainWindow.ForgetHint(15, isPrivate: false));
        Assert.Contains("histórico", MainWindow.ForgetHint(60, isPrivate: false));
        var inPrivate = MainWindow.ForgetHint(15, isPrivate: true);
        Assert.DoesNotContain("todas as janelas", inPrivate);
        Assert.Contains("desta janela", inPrivate);
        Assert.Equal("Os últimos 15 minutos foram esquecidos", MainWindow.ForgetDone(15, isPrivate: false));
        Assert.Equal("A última hora foi esquecida nesta janela", MainWindow.ForgetDone(60, isPrivate: true));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
