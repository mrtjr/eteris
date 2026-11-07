using System.IO;
using System.Windows.Input;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser.Tests;

// Correções da revisão do bloco A na barra lateral: recuo do HTTPS primeiro e página de erro nos sites fixados,
// atalhos do painel, credenciais fora de paineis.json, filtro no histórico inteiro, apagar a linha agrupada do
// histórico e o teclado das listas.
public sealed class ReviewSidebarTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-review-sidebar-" + Guid.NewGuid() + ".db");
    private readonly HistoryStore _store;

    public ReviewSidebarTests() => _store = new HistoryStore(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    // ---------- Página de erro e recuo para http:// (abas e sites fixados) ----------

    [Fact]
    public void Recovery_SiteWithoutHttps_ShowsTheInsecureWarning()
    {
        // O site fixado como http://velho.pt foi subido para https:// e a ligação foi recusada: aviso, não recuo.
        var https = new HttpsFirst();
        var upgraded = https.Upgrade("http://velho.pt/forum")!;
        Assert.Equal(ErrorPage.InsecureUrl(-102, "http://velho.pt/forum"), ErrorPage.Recovery(-102, upgraded, https.FailedUpgrade));
    }

    [Fact]
    public void Recovery_OtherFailures_ShowTheErrorPage_AndAbortsShowNothing()
    {
        var https = new HttpsFirst();
        Assert.Equal(ErrorPage.Url(-105, "https://nao-existe.pt/"), ErrorPage.Recovery(-105, "https://nao-existe.pt/", https.FailedUpgrade));
        // Certificado inválido num site que nunca foi subido: página de erro, não http://.
        Assert.Equal(ErrorPage.Url(-202, "https://mau.pt/"), ErrorPage.Recovery(-202, "https://mau.pt/", https.FailedUpgrade));
        Assert.Null(ErrorPage.Recovery(-3, "https://a.pt/", https.FailedUpgrade));
        Assert.Null(ErrorPage.Recovery(-105, ErrorPage.Url(-105, "https://a.pt/"), https.FailedUpgrade));
    }

    // ---------- Atalhos com o foco num site fixado ----------

    [Theory]
    [InlineData(Key.F5, false, false, WebPanelKey.Reload)]
    [InlineData(Key.R, true, false, WebPanelKey.Reload)]
    [InlineData(Key.R, true, true, WebPanelKey.HardReload)]
    [InlineData(Key.F5, true, false, WebPanelKey.HardReload)]
    [InlineData(Key.F, true, false, WebPanelKey.Find)]
    [InlineData(Key.F3, false, false, WebPanelKey.Find)]
    [InlineData(Key.P, true, false, WebPanelKey.Print)]
    [InlineData(Key.F12, false, false, WebPanelKey.DevTools)]
    [InlineData(Key.I, true, true, WebPanelKey.DevTools)]
    public void PanelKeys_ActOnThePanel(Key key, bool ctrl, bool shift, WebPanelKey expected)
        => Assert.Equal(expected, WebPanelKeys.Resolve(key, ctrl, shift, alt: false));

    [Theory]
    [InlineData(Key.T, true, false, false)]   // nova aba: global
    [InlineData(Key.W, true, false, false)]   // fechar aba: global
    [InlineData(Key.B, true, true, false)]    // barra lateral: global
    [InlineData(Key.F, false, false, true)]   // Alt+F: menu do Éteris
    [InlineData(Key.R, true, false, true)]    // Ctrl+Alt (AltGr) nunca é atalho
    public void PanelKeys_LeaveTheRestGlobal(Key key, bool ctrl, bool shift, bool alt)
        => Assert.Null(WebPanelKeys.Resolve(key, ctrl, shift, alt));

    // ---------- Credenciais no endereço de um site fixado ----------

    [Fact]
    public void Pin_DropsCredentialsFromTheAddress()
    {
        const string withPassword = "http://admin:senha@192.168.1.10/painel";
        Assert.True(WebPanels.HasCredentials(withPassword));
        var list = WebPanels.Add([], withPassword, "NAS")!;
        var saved = WebPanels.Serialize(list);
        Assert.DoesNotContain("senha", saved);
        Assert.DoesNotContain("admin", saved);
        Assert.Equal("http://192.168.1.10/painel", Assert.Single(list).Url);
        // A chave lembrada em settings.json («web:<endereço>») sai do mesmo endereço limpo.
        Assert.DoesNotContain("senha", SidebarPanels.WebKey(WebPanels.Normalize(withPassword)!));
        Assert.False(WebPanels.HasCredentials("https://a.pt/"));
    }

    [Fact]
    public void Parse_OldFileWithCredentials_IsCleaned()
    {
        var panels = WebPanels.Parse("""[{"url":"https://eu:segredo@a.pt/x","title":"A"}]""");
        Assert.Equal("https://a.pt/x", Assert.Single(panels).Url);
    }

    // ---------- Filtro do Histórico: o histórico inteiro ----------

    [Fact]
    public void HistoryFilter_FindsVisitsOlderThanTheRecentWindow()
    {
        var old = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        _store.Record("https://receitas.pt/bacalhau", "Receita de bacalhau à Brás", old);
        for (var i = 0; i < 450; i++)
            _store.Record($"https://noticias.pt/{i}", $"Notícia {i}", old.AddDays(3).AddMinutes(i));

        // As 400 visitas mais recentes já não chegam à receita…
        Assert.DoesNotContain(_store.Visits(400), v => v.Url.Contains("bacalhau"));
        // …mas o filtro procura no histórico todo, com as regras do filtro das abas (palavras soltas, sem acentos).
        var hits = _store.VisitsMatching((title, url) => SidebarLists.Matches(title, url, "bras receita"), 400);
        Assert.Equal("https://receitas.pt/bacalhau", Assert.Single(hits).Url);
    }

    [Fact]
    public void HistoryFilter_KeepsNewestFirstAndTheLimit()
    {
        var t = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++) _store.Record($"https://a.pt/{i}", "Alfa", t.AddHours(i));
        _store.Record("https://b.pt/", "Beta", t.AddHours(9));
        var hits = _store.VisitsMatching((title, url) => SidebarLists.Matches(title, url, "alfa"), 3);
        Assert.Equal(["https://a.pt/4", "https://a.pt/3", "https://a.pt/2"], hits.Select(v => v.Url));
    }

    // ---------- Apagar a linha agrupada do histórico ----------

    [Fact]
    public void DeletingAGroupedHistoryRow_RemovesEveryVisitOfThatDay()
    {
        var zone = TimeZoneInfo.Utc;
        var today = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
        _store.Record("https://a.pt/", "A", today.AddHours(10).AddMinutes(2));
        _store.Record("https://a.pt/", "A", today.AddHours(11).AddMinutes(30));
        _store.Record("https://a.pt/", "A", today.AddHours(14).AddMinutes(15));
        _store.Record("https://a.pt/", "A", today.AddDays(-1).AddHours(20));   // ontem: fica
        _store.Record("https://b.pt/", "B", today.AddHours(12));               // outra página: fica

        var now = today.AddHours(15);
        var rows = SidebarLists.History(_store.Visits(400), now, "", zone);
        var row = rows.OfType<SidebarLink>().First(l => l.Url == "https://a.pt/");
        Assert.Equal("a.pt · 14:15", row.Subline);

        var (from, to) = SidebarLists.DayRangeUtc(row.VisitedUtc, zone);
        _store.DeleteVisits(row.Url, from, to);

        var after = SidebarLists.History(_store.Visits(400), now, "", zone);
        Assert.Equal(["Hoje", "B", "Ontem", "A"], after.Select(r => r is SidebarHeading h ? h.Title : ((SidebarLink)r).Title));
    }

    [Fact]
    public void DayRange_IsTheLocalDayInUtc()
    {
        var lisbon = TimeZoneInfo.CreateCustomTimeZone("UTC+1", TimeSpan.FromHours(1), "UTC+1", "UTC+1");
        // 23:30 UTC de dia 6 = 00:30 de dia 7 em UTC+1: o dia é o 7 local, das 23:00 UTC de 6 às 23:00 UTC de 7.
        var (from, to) = SidebarLists.DayRangeUtc(new DateTime(2026, 11, 6, 23, 30, 0, DateTimeKind.Utc), lisbon);
        Assert.Equal(new DateTime(2026, 11, 6, 23, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 11, 7, 23, 0, 0, DateTimeKind.Utc), to);
    }

    // ---------- Teclado das listas ----------

    [Fact]
    public void Cursor_SkipsDayHeadingsAndStopsAtTheEnds()
    {
        IReadOnlyList<object> rows =
        [
            new SidebarHeading("Hoje"),
            new SidebarLink("https://a.pt/", "A", "a.pt", "", false),
            new SidebarHeading("Ontem"),
            new SidebarLink("https://b.pt/", "B", "b.pt", "", false),
            new SidebarLink("https://c.pt/", "C", "c.pt", "", false),
        ];
        Assert.Equal(1, SidebarLists.MoveCursor(rows, -1, 1));   // sem escolha: a primeira linha
        Assert.Equal(1, SidebarLists.MoveCursor(rows, -1, -1));
        Assert.Equal(3, SidebarLists.MoveCursor(rows, 1, 1));    // salta «Ontem»
        Assert.Equal(1, SidebarLists.MoveCursor(rows, 3, -1));
        Assert.Equal(4, SidebarLists.MoveCursor(rows, 4, 1));    // na ponta fica
        Assert.Equal(1, SidebarLists.MoveCursor(rows, 1, -1));
        Assert.Equal(-1, SidebarLists.MoveCursor([new SidebarHeading("Hoje")], -1, 1));
    }

    [Fact]
    public void Cursor_RaisesChangeForTheHighlight()
    {
        var link = new SidebarLink("https://a.pt/", "A", "a.pt", "", false);
        var changed = 0;
        link.PropertyChanged += (_, _) => changed++;
        link.IsCurrent = true;
        link.IsCurrent = true;
        Assert.Equal(1, changed);
        // Identidade: a mesma página em duas linhas são duas linhas.
        Assert.NotEqual(link, new SidebarLink("https://a.pt/", "A", "a.pt", "", false));
    }

    [Fact]
    public void ReadingList_HasAFilterToo()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var rows = SidebarLists.Reading(
        [
            new ReadingItem("https://a.pt/", "Pão de ló", now.AddHours(-2), false),
            new ReadingItem("https://b.pt/", "Bolo-rei", now.AddHours(-1), false),
        ], now, "pao");
        Assert.Equal("Pão de ló", Assert.Single(rows).Title);
    }
}
