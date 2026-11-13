using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// As vigias e as leituras de preço no eteris.db: uma vigia nova verifica-se já; gravar uma verificação numa vigia apagada
// (a meio de uma visita, ou com o Éteris a fechar) não a ressuscita; as bloqueadas, as sem pedaço e as pausadas não
// voltam à agenda; as leituras guardam-se 90 dias; «Limpar dados» leva só as leituras e «Esquecer este site» as vigias
// desse site; nunca na janela privada.
public sealed class WatchStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-watches-" + Guid.NewGuid() + ".db");
    private readonly WatchStore _store;

    public WatchStoreTests() => _store = new WatchStore(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static NewWatch New(string url = "https://www.kabum.com.br/produto/400812")
        => new(url, "SSD 1 TB", WatchKind.Price, null, false, 120, null);

    private static WatchCheck Ok(DateTime at, decimal price) => new(at, WatchStatus.Ok, price, "BRL", null, PageBlock.None);

    [Fact] public void Add_ThenDueImmediately()
    {
        var id = _store.Add(New("https://www.kabum.com.br/produto/400812"), Now);
        Assert.Equal(id, Assert.Single(_store.Due(Now)).Id);
    }

    [Fact] public void Add_KeepsWhatWasAsked()
    {
        var id = _store.Add(new NewWatch("https://www.pichau.com.br/ssd", "SSD", WatchKind.Change, "#stock > span", true, 240, 499.90m), Now);
        var w = Assert.Single(_store.All());
        Assert.Equal((id, "https://www.pichau.com.br/ssd", "pichau.com.br", "SSD", WatchKind.Change, "#stock > span", true, 240, 499.90m),
            (w.Id, w.Url, w.Site, w.Title, w.Kind, w.Selector, w.UseSession, w.IntervalMinutes, w.TargetPrice));
        Assert.Equal((false, WatchStatus.New, 0, Now, (DateTime?)null, Now, PageBlock.None),
            (w.Paused, w.Status, w.Failures, w.CreatedUtc, w.LastCheckUtc, w.NextCheckUtc, w.LastBlock));
    }

    [Fact] public void Add_NeverBelowTheMinimumInterval()
    {
        _store.Add(New() with { IntervalMinutes = 5 }, Now);
        Assert.Equal(WatchSchedule.MinIntervalMinutes, Assert.Single(_store.All()).IntervalMinutes);
    }

    [Fact] public void Record_UpdatesStatusPriceAndNext()
    {
        var id = _store.Add(New(), Now);
        Assert.True(_store.Record(id, new WatchCheck(Now, WatchStatus.Ok, 588.22m, "BRL", null, PageBlock.None), Now.AddHours(2)));
        var w = Assert.Single(_store.All());
        Assert.Equal((WatchStatus.Ok, 588.22m, "BRL", Now.AddHours(2)), (w.Status, w.LastPrice, w.Currency, w.NextCheckUtc));
        Assert.Empty(_store.Due(Now.AddHours(1)));
        Assert.Single(_store.Prices(id));
    }

    // Uma falha não apaga a última leitura boa; conta as falhas seguidas (WatchRules e WatchSchedule usam a contagem) e
    // lembra desde quando falha; uma leitura boa volta a pôr tudo a zero.
    [Fact] public void Record_FailureKeepsTheLastGoodReadingAndCounts()
    {
        var id = _store.Add(New(), Now);
        _store.Record(id, Ok(Now, 588.22m), Now.AddHours(2));
        _store.Record(id, new WatchCheck(Now.AddHours(2), WatchStatus.Ok, null, null, null, PageBlock.HttpError), Now.AddHours(6));
        _store.Record(id, new WatchCheck(Now.AddHours(6), WatchStatus.Failing, null, null, null, PageBlock.HttpError), Now.AddHours(14));
        var w = Assert.Single(_store.All());
        Assert.Equal((WatchStatus.Failing, 588.22m, "BRL", 2, (DateTime?)Now.AddHours(2), PageBlock.HttpError, (DateTime?)Now.AddHours(6)),
            (w.Status, w.LastPrice, w.Currency, w.Failures, w.FailingSinceUtc, w.LastBlock, w.LastCheckUtc));
        Assert.Single(_store.Prices(id));

        _store.Record(id, Ok(Now.AddHours(14), 579m), Now.AddHours(16));
        w = Assert.Single(_store.All());
        Assert.Equal((WatchStatus.Ok, 579m, 0, (DateTime?)null, PageBlock.None), (w.Status, w.LastPrice, w.Failures, w.FailingSinceUtc, w.LastBlock));
    }

    [Fact] public void Record_KeepsTheTextOfAChangeWatch()
    {
        var id = _store.Add(New() with { Kind = WatchKind.Change, Selector = "#stock" }, Now);
        _store.Record(id, new WatchCheck(Now, WatchStatus.Ok, null, null, "Esgotado", PageBlock.None), Now.AddHours(2));
        Assert.Equal("Esgotado", Assert.Single(_store.All()).LastText);
        Assert.Empty(_store.Prices(id));
    }

    [Fact] public void Record_OnDeletedWatch_ReturnsFalse()
    {
        var id = _store.Add(New(), Now); _store.Delete(id);
        Assert.False(_store.Record(id, new WatchCheck(Now, WatchStatus.Ok, 1m, null, null, PageBlock.None), Now.AddHours(2)));
        Assert.Empty(_store.All()); Assert.Empty(_store.Prices(id));
    }

    [Fact] public void Delete_TakesTheReadingsWithIt()
    {
        var keep = _store.Add(New("https://b.pt/"), Now); _store.Record(keep, Ok(Now, 5m), Now.AddHours(2));
        var id = _store.Add(New(), Now); _store.Record(id, Ok(Now, 9m), Now.AddHours(2));
        _store.Delete(id);
        Assert.Empty(_store.Prices(id));
        Assert.Single(_store.Prices(keep));
        Assert.Equal(1, _store.DeleteReadingsSince(DateTime.MinValue));
    }

    // Um id apagado não se reaproveita: a verificação atrasada de uma vigia apagada não cai numa vigia nova.
    [Fact] public void Record_OnDeletedWatch_DoesNotLandOnANewOne()
    {
        var old = _store.Add(New("https://a.pt/"), Now); _store.Delete(old);
        var fresh = _store.Add(New("https://b.pt/"), Now);
        Assert.NotEqual(old, fresh);
        Assert.False(_store.Record(old, Ok(Now, 1m), Now.AddHours(2)));
        Assert.Equal(WatchStatus.New, Assert.Single(_store.All()).Status);
    }

    [Fact] public void BlockedAndPaused_AreNotDue()
    {
        var a = _store.Add(New("https://a.pt/"), Now); var b = _store.Add(New("https://b.pt/"), Now);
        _store.Record(a, new WatchCheck(Now, WatchStatus.Blocked, null, null, null, PageBlock.Challenge), DateTime.MaxValue);
        _store.SetPaused(b, true);
        Assert.Empty(_store.Due(Now.AddDays(30)));
        Assert.Equal(DateTime.MaxValue, _store.All().Single(w => w.Id == a).NextCheckUtc);
    }

    [Fact] public void Missing_IsNotDue_AndUnpausedComesBack()
    {
        var a = _store.Add(New("https://a.pt/"), Now); var b = _store.Add(New("https://b.pt/"), Now);
        _store.Record(a, new WatchCheck(Now, WatchStatus.Missing, null, null, null, PageBlock.None), Now);
        _store.SetPaused(b, true);
        _store.SetPaused(b, false);
        Assert.Equal(b, Assert.Single(_store.Due(Now)).Id);
    }

    [Fact] public void ForUrl_FindsEveryStatus()
    {
        var a = _store.Add(New("https://a.pt/p"), Now); _store.Add(New("https://b.pt/p"), Now);
        _store.Record(a, new WatchCheck(Now, WatchStatus.Blocked, null, null, null, PageBlock.Challenge), DateTime.MaxValue);
        Assert.Equal(a, Assert.Single(_store.ForUrl("https://a.pt/p")).Id);
        Assert.Empty(_store.ForUrl("https://c.pt/p"));
    }

    [Fact] public void Readings_OlderThan90Days_ArePruned()
    {
        var id = _store.Add(New(), Now.AddDays(-100));
        _store.Record(id, Ok(Now.AddDays(-100), 10m), Now.AddDays(-99));
        _store.Record(id, Ok(Now, 9m), Now.AddHours(2));
        Assert.Equal([9m], _store.Prices(id).Select(p => p.Price));
    }

    // A linha de base guarda a fonte (aba ou motor): uma leitura que a substitui grava a sua; uma verificação sem preço
    // nem texto (a aba contra a linha de base do motor, uma falha) deixa-a como estava.
    [Fact] public void Record_KeepsWhereTheBaselineCameFrom()
    {
        var id = _store.Add(New(), Now);
        _store.Record(id, Ok(Now, 90m) with { FromTab = true }, Now.AddHours(2));
        Assert.True(Assert.Single(_store.All()).BaselineFromTab);

        _store.Record(id, Ok(Now.AddHours(2), 100m), Now.AddHours(4));
        Assert.False(Assert.Single(_store.All()).BaselineFromTab);

        _store.Record(id, new WatchCheck(Now.AddHours(3), WatchStatus.Ok, null, null, null, PageBlock.None, FromTab: true), Now.AddHours(5));
        var w = Assert.Single(_store.All());
        Assert.Equal((false, (decimal?)100m), (w.BaselineFromTab, w.LastPrice));
        Assert.Equal(2, _store.Prices(id).Count);
    }

    // Um eteris.db de uma versão anterior desta etapa (sem a coluna) abre e ganha-a.
    [Fact] public void OldTable_GainsTheBaselineColumn()
    {
        var path = Path.Combine(Path.GetTempPath(), "eteris-watches-old-" + Guid.NewGuid() + ".db");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE watches(
                        id INTEGER PRIMARY KEY AUTOINCREMENT, url TEXT NOT NULL, site TEXT NOT NULL, title TEXT NOT NULL,
                        kind TEXT NOT NULL, selector TEXT, use_session INTEGER NOT NULL, interval_min INTEGER NOT NULL,
                        target_price TEXT, paused INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL, failures INTEGER NOT NULL DEFAULT 0,
                        last_block TEXT NOT NULL, created_at INTEGER NOT NULL, last_check_at INTEGER, next_check_at INTEGER NOT NULL,
                        last_text TEXT, last_price TEXT, currency TEXT, failing_since INTEGER);
                    INSERT INTO watches(url, site, title, kind, use_session, interval_min, status, last_block, created_at, next_check_at)
                    VALUES ('https://a.pt/p', 'a.pt', 'P', 'Price', 0, 120, 'Ok', 'None', 0, 0);
                    """;
                command.ExecuteNonQuery();
            }
            var store = new WatchStore(path);
            Assert.False(Assert.Single(store.All()).BaselineFromTab);
            Assert.True(store.Record(Assert.Single(store.All()).Id, Ok(Now, 9m) with { FromTab = true }, Now.AddHours(2)));
            Assert.True(Assert.Single(store.All()).BaselineFromTab);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact] public void Prices_InOrder_WithTheirTimes()
    {
        var id = _store.Add(New(), Now);
        _store.Record(id, Ok(Now, 10m), Now.AddHours(2));
        _store.Record(id, Ok(Now.AddHours(2), 9.5m), Now.AddHours(4));
        Assert.Equal([(Now, 10m), (Now.AddHours(2), 9.5m)], _store.Prices(id));
    }

    [Fact] public void DeleteReadingsSince_KeepsTheWatch()
    {
        var id = _store.Add(New(), Now); _store.Record(id, Ok(Now, 9m), Now.AddHours(2));
        Assert.Equal(1, _store.DeleteReadingsSince(Now.AddMinutes(-1)));
        Assert.Single(_store.All()); Assert.Empty(_store.Prices(id));
    }

    [Fact] public void DeleteReadingsSince_TheBeginning_TakesThemAll()
    {
        var id = _store.Add(New(), Now.AddDays(-10));
        _store.Record(id, Ok(Now.AddDays(-10), 10m), Now.AddDays(-9));
        _store.Record(id, Ok(Now, 9m), Now.AddHours(2));
        Assert.Equal(2, _store.DeleteReadingsSince(DateTime.MinValue));
    }

    // Achado 7: as vigias ficam (são ferramentas, como os favoritos), mas a linha de uma vigia verificada no período não
    // guarda o que foi lido nele: o último preço, o texto do pedaço (que pode vir de uma aba com a sessão) e a hora da
    // verificação saem com as leituras. Uma vigia verificada antes do período fica como estava.
    [Fact] public void DeleteReadingsSince_ForgetsWhatTheWatchesReadInThePeriod()
    {
        var recent = _store.Add(New("https://www.kabum.com.br/produto/1"), Now.AddDays(-3));
        _store.Record(recent, Ok(Now, 9m) with { FromTab = true }, Now.AddHours(2));
        var change = _store.Add(new NewWatch("https://www.pichau.com.br/ssd", "SSD", WatchKind.Change, "#estado", false, 120, null), Now.AddDays(-3));
        _store.Record(change, new WatchCheck(Now, WatchStatus.Ok, null, null, "Olá, Ana — em stock", PageBlock.None), Now.AddHours(2));
        var old = _store.Add(New("https://www.terabyte.com.br/p"), Now.AddDays(-3));
        _store.Record(old, Ok(Now.AddDays(-2), 50m), Now.AddHours(2));

        _store.DeleteReadingsSince(Now.AddHours(-1));

        var all = _store.All().ToDictionary(w => w.Id);
        Assert.Equal(3, all.Count);
        foreach (var id in new[] { recent, change })
        {
            var w = all[id];
            Assert.Equal(((decimal?)null, (string?)null, (DateTime?)null, false), (w.LastPrice, w.LastText, w.LastCheckUtc, w.BaselineFromTab));
        }
        Assert.Equal(((decimal?)50m, (DateTime?)Now.AddDays(-2)), (all[old].LastPrice, all[old].LastCheckUtc));
    }

    // Achado 6: os três caminhos que apagam o histórico (Definições › Apagar dados, eteris://history › Limpar, «Esquecer
    // os últimos N minutos» da paleta) passam todos por AppData.ForgetSince, e é lá que as leituras das vigias saem.
    [Fact] public void ForgetSince_TakesTheWatchReadings()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Data", "AppData.cs"));
        var forget = app[app.IndexOf("public static void ForgetSince(DateTime sinceUtc)", StringComparison.Ordinal)..];
        forget = forget[..forget.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("DeleteWatchReadingsSince(sinceUtc);", forget);

        var scheme = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        Assert.DoesNotContain("DeleteWatchReadingsSince(", scheme);
        var history = scheme[scheme.IndexOf("case \"/clear\" when HistoryPage.IsAuthorized(url)", StringComparison.Ordinal)..];
        Assert.Contains("AppData.ForgetSince(from);", history[..history.IndexOf("break;", StringComparison.Ordinal)]);
    }

    // O painel «Apagar dados» diz o que acontece às vigias com a caixa do histórico.
    [Fact] public void ClearDataPanel_SaysTheWatchesStay()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "settings.html"));
        var box = settings[settings.IndexOf("value=\"history\"", StringComparison.Ordinal)..];
        box = box[..box.IndexOf("</label>", StringComparison.Ordinal)];
        Assert.Contains("as vigias ficam; os preços e textos que leram no período saem", box);
    }

    [Fact] public void DeleteSite_RemovesOnlyThatSite()
    {
        _store.Add(New("https://www.kabum.com.br/produto/1"), Now); _store.Add(New("https://loja.kabum.com.br/p"), Now);
        _store.Add(New("https://www.pichau.com.br/ssd"), Now);
        Assert.Equal(2, _store.DeleteSite("kabum.com.br"));
        Assert.Equal("https://www.pichau.com.br/ssd", Assert.Single(_store.All()).Url);
    }

    [Fact] public void DeleteSite_TakesTheReadingsWithIt()
    {
        var id = _store.Add(New("https://www.kabum.com.br/produto/1"), Now);
        _store.Record(id, Ok(Now, 9m), Now.AddHours(2));
        _store.DeleteSite("kabum.com.br");
        Assert.Empty(_store.Prices(id));
    }

    // Os preços guardam-se como texto invariante: lidos num Windows em português, 1.299,90 continua 1299,90.
    [Fact] public void Prices_SurviveAPortugueseCulture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
            var id = _store.Add(New() with { TargetPrice = 1199.5m }, Now);
            _store.Record(id, Ok(Now, 1299.90m), Now.AddHours(2));
            var w = Assert.Single(_store.All());
            Assert.Equal((1299.90m, 1199.5m), (w.LastPrice, w.TargetPrice));
            Assert.Equal(1299.90m, Assert.Single(_store.Prices(id)).Price);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    // O ficheiro guarda endereços e o texto das páginas: o que se apaga é substituído por zeros, como no histórico.
    [Fact] public void Store_OpensWithSecureDelete()
        => Assert.Contains("SqliteSetup.SecureDelete(connection);", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Data", "WatchStore.cs")));

    [Fact] public void AppData_AddWatch_RefusesPrivate() => Assert.Null(AppData.AddWatch(New(), isPrivate: true));

    // «Limpar dados › histórico» leva as leituras de preço do período (as vigias ficam, como os favoritos), e só a
    // partir de uma janela normal (por AppData.ForgetSince); «Esquecer este site» leva as vigias do site, também só fora
    // da janela privada.
    [Fact] public void ClearData_AndForgetSite_ReachTheWatches()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs"));
        var clear = code[code.IndexOf("private static async Task<string> ClearBrowsingDataAsync(", StringComparison.Ordinal)..];
        clear = clear[clear.IndexOf("if (request.History && !privateContext)", StringComparison.Ordinal)..];
        clear = clear[..clear.IndexOf("\n        }", StringComparison.Ordinal)];
        Assert.Contains("AppData.ForgetSince(since);", clear);

        var forget = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Forget.cs"));
        var shred = forget[forget.IndexOf("private async Task<ShredResult> ShredSiteAsync(", StringComparison.Ordinal)..];
        shred = shred[shred.IndexOf("if (!IsPrivate)", StringComparison.Ordinal)..];
        shred = shred[..shred.IndexOf("\n        else", StringComparison.Ordinal)];
        Assert.Contains("AppData.DeleteWatchesOfSite(site);", shred);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
