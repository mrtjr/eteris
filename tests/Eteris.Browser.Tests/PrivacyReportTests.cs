using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B10 — relatório de privacidade (eteris://privacy/): agregados por dia, categoria e domínio, nunca sites visitados.
public class PrivacyReportTests
{
    private static readonly DateOnly Today = new(2026, 11, 7);

    private static List<PrivacyEvent> Sample() =>
    [
        new(Today, PrivacyCategory.Lists, "google-analytics.com", 40),
        new(Today, PrivacyCategory.Lists, "doubleclick.net", 20),
        new(Today.AddDays(-1), PrivacyCategory.Lists, "facebook.net", 25),
        new(Today.AddDays(-2), PrivacyCategory.Cloaked, "eulerian.net", 3),
        new(Today.AddDays(-2), PrivacyCategory.LocalNetwork, "", 2),
        new(Today.AddDays(-3), PrivacyCategory.Debounce, "google.com", 4),
        new(Today.AddDays(-4), PrivacyCategory.Forgotten, "rastreio.example", 1),
        new(Today.AddDays(-6), PrivacyCategory.User, "criteo.com", 10),
        new(Today.AddDays(-7), PrivacyCategory.Lists, "google-analytics.com", 500),
        new(Today.AddDays(-20), PrivacyCategory.Lists, "criteo.net", 7),
    ];

    [Fact]
    public void Week_CountsOnlyTheLastSevenDays_PerDayAndPerCategory()
    {
        var r = PrivacyReport.Build(Sample(), Today, 7);
        // A rede local (2) não é rastreio: fica só no seu mosaico.
        Assert.Equal(98, r.Blocked);
        Assert.Equal([10, 0, 0, 0, 3, 25, 60], r.PerDay);
        Assert.Equal(2, r.ByCategory[PrivacyCategory.LocalNetwork]);
        Assert.Equal(60, r.ByCategory[PrivacyCategory.Lists] - 25);
        Assert.Equal(4, r.ByCategory[PrivacyCategory.Debounce]);
        Assert.Equal(1, r.ByCategory[PrivacyCategory.Forgotten]);
        Assert.Equal([("Google", 60), ("Meta", 25), ("Criteo", 10), ("Eulerian", 3)], r.TopCompanies);
        Assert.Equal(("google-analytics.com", "Google", 40), r.TopTrackers[0]);
        Assert.DoesNotContain(r.TopTrackers, t => t.Domain.Length == 0);
        Assert.Equal(["eulerian.net"], r.CloakedExamples);
        Assert.Equal("Nos últimos 7 dias, o Escudo travou 98 pedidos de rastreio — 61 % vinham da Google e saltou 4 redirecionamentos de rastreio.", r.Summary);
    }

    [Fact]
    public void Month_IncludesTheOlderDays()
    {
        var r = PrivacyReport.Build(Sample(), Today, 30);
        Assert.Equal(605, r.Blocked);
        Assert.Equal(30, r.PerDay.Count);
        Assert.Equal(500, r.PerDay[22]);
        Assert.Equal("Nos últimos 30 dias, o Escudo travou 605 pedidos de rastreio — 93 % vinham da Google e saltou 4 redirecionamentos de rastreio.", r.Summary);
    }

    [Fact]
    public void Empty_SaysItFillsUpAsYouBrowse()
    {
        var r = PrivacyReport.Build([], Today, 7);
        Assert.Equal(0, r.Blocked);
        Assert.Equal("Nos últimos 7 dias ainda não há nada para contar: o relatório enche-se à medida que navega.", r.Summary);
        Assert.Equal("1 234", PrivacyReport.Number(1234).Replace(' ', ' ').Replace(' ', ' '));
    }

    // Revisão aos 50: o modo rigoroso (CDNs, embeds) e a rede local não entram nos «pedidos de rastreio».
    [Fact]
    public void StrictAndLocalNetwork_StayOutOfTheTrackingTotal()
    {
        List<PrivacyEvent> events =
        [
            new(Today, PrivacyCategory.Lists, "doubleclick.net", 10),
            new(Today, PrivacyCategory.Strict, "js.stripe.com", 30),
            new(Today, PrivacyCategory.Strict, "youtube.com", 5),
            new(Today, PrivacyCategory.LocalNetwork, "", 4),
        ];
        var r = PrivacyReport.Build(events, Today, 7);
        Assert.Equal(10, r.Blocked);
        Assert.Equal(10, r.PerDay[^1]);
        Assert.Equal([("doubleclick.net", "Google", 10)], r.TopTrackers);
        Assert.Equal([("Google", 10)], r.TopCompanies);
        Assert.Equal(35, r.ByCategory[PrivacyCategory.Strict]);
        Assert.Equal("Nos últimos 7 dias, o Escudo travou 10 pedidos de rastreio — 100 % vinham da Google; o modo rigoroso travou mais 35 pedidos a outros domínios.", r.Summary);
    }

    [Theory]
    [InlineData("https://autoteste-eteris.invalid/", false)]
    [InlineData("https://terceiro-eteris.invalid/eco", false)]
    [InlineData("https://jornal.pt/", true)]
    [InlineData(null, true)]
    public void Ledger_SkipsTheSelfTest(string? pageUrl, bool counts) => Assert.Equal(counts, PrivacyLedger.Counts(pageUrl));

    [Fact]
    public void Store_Accumulates_Prunes_AndClears()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eteris-report-{Guid.NewGuid():N}.db");
        try
        {
            var store = new PrivacyEventsStore(path);
            store.Add(Today, [(PrivacyCategory.Lists, "doubleclick.net", 3)]);
            store.Add(Today, [(PrivacyCategory.Lists, "doubleclick.net", 2), (PrivacyCategory.Debounce, "google.com", 1)]);
            store.Add(Today.AddDays(-40), [(PrivacyCategory.Lists, "criteo.net", 9)]);
            Assert.Equal(5, store.Since(Today).Single(e => e.Subject == "doubleclick.net").Count);
            Assert.Equal(3, store.Since(Today.AddDays(-60)).Count);
            store.Prune(Today);
            Assert.Equal(2, store.Since(Today.AddDays(-60)).Count);
            store.Clear();
            Assert.Empty(store.Since(Today.AddDays(-60)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static PrivacyPage.State State() => new(
        new Dictionary<string, string>(), PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
        SecureDns: null, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);

    [Fact]
    public void Page_CarriesBothRanges_AndNothingInThePrivateWindow()
    {
        var html = PrivacyPage.Render(State(), Sample(), Today);
        Assert.Contains("\"week\":{\"days\":7,\"blocked\":98", html);
        Assert.Contains("\"month\":{\"days\":30,\"blocked\":605", html);
        Assert.Contains("\"label\":\"Escondidos por CNAME\"", html);
        Assert.Contains("id=\"relatorio\"", html);
        Assert.Contains("Apagar o relatório", html);
        Assert.Contains("\"report\":null", PrivacyPage.Render(State()));
        Assert.Contains("Na janela privada o relatório não é mostrado nem alimentado", html);
        Assert.Contains("url=eteris://privacy/#relatorio", PrivacyPage.ReportClearedRedirect);
    }

    // Nada da janela privada entra no relatório, e apagar só funciona com o token da sessão e fora da privada.
    [Fact]
    public void PrivateWindows_NeverFeedTheReport_AndClearingNeedsTheToken()
    {
        var handler = Read("src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs");
        Assert.Contains("if (isPrivate || PrivacyCategory.Of(verdict) is not { } category || !PrivacyLedger.Counts(pageUrl)) return;", handler);
        Assert.Contains("if (!isPrivate)\n                foreach (var host in pending.Hosts) PrivacyLedger.Count(PrivacyCategory.Debounce, host);", handler.Replace("\r\n", "\n"));
        var factory = Read("src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs");
        Assert.Contains("if (!privateContext && InternalPages.IsAuthorized(request.Url)) AppData.ClearPrivacyReport();", factory);
        Assert.Contains("privateContext ? null : AppData.PrivacyEvents(30)", factory);
    }

    // Os totais ficam e os domínios saem: «Apagar histórico» (desde o dia), «Esquecer» e «Triturar» (um site).
    [Fact]
    public void Store_ForgetSubjectsSince_KeepsTotalsAndDropsDomains()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eteris-report-{Guid.NewGuid():N}.db");
        try
        {
            var store = new PrivacyEventsStore(path);
            store.Add(Today, [(PrivacyCategory.Lists, "exemplo.pt", 3), (PrivacyCategory.Lists, "", 2), (PrivacyCategory.Cloaked, "t.net", 1)]);
            store.Add(Today.AddDays(-3), [(PrivacyCategory.Lists, "antigo.pt", 4)]);
            store.ForgetSubjectsSince(Today);
            var rows = store.Since(Today.AddDays(-10)).OrderBy(e => e.Day).ThenBy(e => e.Category).ToList();
            Assert.Equal(
                [new PrivacyEvent(Today.AddDays(-3), PrivacyCategory.Lists, "antigo.pt", 4), new PrivacyEvent(Today, PrivacyCategory.Cloaked, "", 1), new PrivacyEvent(Today, PrivacyCategory.Lists, "", 5)],
                rows);
            store.Add(Today, [(PrivacyCategory.Lists, "outro.pt", 1)]);
            store.ForgetSubject("antigo.pt");
            Assert.Equal(["", "", "", "outro.pt"], store.Since(Today.AddDays(-10)).Select(e => e.Subject).Order(StringComparer.Ordinal));
            Assert.Equal(11, store.Since(Today.AddDays(-10)).Sum(e => e.Count));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void ClearingHistoryAndShredding_AlsoForgetTheReportDomains()
    {
        Assert.Contains("PrivacyLedger.SubjectFor(pageUrl, detail ?? SiteObservatory.HostOf(url).ToString())", Read("src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        var data = Read("src", "Eteris.Browser", "Data", "AppData.cs");
        Assert.Contains("ForgetSubjectsSince(", data);
        Assert.Contains("AppData.ForgetPrivacySubject(site);", Read("src", "Eteris.Browser", "MainWindow.Forget.cs"));
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}

// O livro (PrivacyLedger) é um só, estático, e os testes do PrivacyRequestHandler contam nele em paralelo: entre o
// primeiro Take() e o último um desses pedidos entrava no livro (falha intermitente). Este corre sozinho.
[Collection(GlobalStateCollection.Name)]
public class PrivacyLedgerTests
{
    [Fact]
    public void SubjectFor_DropsTheDomain_WhenTheBlockedRequestIsFirstParty()
    {
        Assert.Null(PrivacyLedger.SubjectFor("https://www.jornal.pt/artigo", "www.jornal.pt"));
        Assert.Null(PrivacyLedger.SubjectFor("https://www.jornal.pt/artigo", "metrics.jornal.pt"));
        Assert.Equal("www.google-analytics.com", PrivacyLedger.SubjectFor("https://www.jornal.pt/artigo", "www.google-analytics.com"));
        Assert.Equal("x.pt", PrivacyLedger.SubjectFor(null, "x.pt"));
        Assert.Null(PrivacyLedger.SubjectFor("https://www.jornal.pt/", ""));
    }

    [Fact]
    public void Ledger_AggregatesBySiteOfTheTracker()
    {
        PrivacyLedger.Take();
        PrivacyLedger.Count(PrivacyCategory.Lists, "www.google-analytics.com");
        PrivacyLedger.Count(PrivacyCategory.Lists, "region1.google-analytics.com");
        PrivacyLedger.Count(PrivacyCategory.LocalNetwork, null);
        var taken = PrivacyLedger.Take().OrderBy(t => t.Category).ToList();
        Assert.Equal([(PrivacyCategory.Lists, "google-analytics.com", 2), (PrivacyCategory.LocalNetwork, "", 1)], taken);
        Assert.Empty(PrivacyLedger.Take());
    }
}
