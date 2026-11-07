using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabRadarTests
{
    private static readonly IReadOnlyList<ClosedTab> NoClosed = [];

    private static BrowserTab Tab(string address, string title, int minutesAgo = 0)
        => new(address) { Title = title, LastActiveUtc = DateTime.UtcNow.AddMinutes(-minutesAgo) };

    private static IReadOnlyList<RadarRow> Rows(IReadOnlyList<BrowserTab> tabs, RadarScope scope = RadarScope.All,
        RadarSort sort = RadarSort.Strip, string query = "", BrowserTab? active = null, IReadOnlyList<ClosedTab>? closed = null)
        => TabRadar.Rows(tabs, active, closed ?? NoClosed, scope, sort, query);

    private static string[] Titles(IEnumerable<RadarRow> rows) => rows.Select(r => r.Title).ToArray();

    // ---------- Âmbitos ----------

    [Fact]
    public void Scope_All_ListsEveryOpenTabInStripOrder()
    {
        var tabs = new[] { Tab("https://a.pt/", "A"), Tab("https://b.pt/", "B"), Tab("eteris://newtab/", "Nova aba") };
        Assert.Equal(["A", "B", "Nova aba"], Titles(Rows(tabs)));
    }

    [Fact]
    public void Scope_Pinned_KeepsOnlyPinnedTabs()
    {
        var pinned = Tab("https://a.pt/", "A");
        pinned.IsPinned = true;
        var rows = Rows([pinned, Tab("https://b.pt/", "B")], RadarScope.Pinned);
        Assert.Equal(["A"], Titles(rows));
    }

    [Fact]
    public void Scope_Audible_ExcludesMutedTabs()
    {
        var playing = Tab("https://a.pt/", "A");
        playing.IsAudible = true;
        var muted = Tab("https://b.pt/", "B");
        muted.IsAudible = true;
        muted.IsMuted = true;
        var rows = Rows([playing, muted, Tab("https://c.pt/", "C")], RadarScope.Audible);
        Assert.Equal(["A"], Titles(rows));
    }

    [Fact]
    public void Scope_Sleeping_KeepsOnlySleepingTabs()
    {
        var asleep = Tab("https://a.pt/", "A");
        asleep.IsSleeping = true;
        var rows = Rows([Tab("https://b.pt/", "B"), asleep], RadarScope.Sleeping);
        Assert.Equal(["A"], Titles(rows));
    }

    [Fact]
    public void Scope_Duplicates_GroupsTabsWithTheSameWebAddress()
    {
        var tabs = new[] { Tab("https://a.pt", "A1"), Tab("https://a.pt/", "A2"), Tab("https://b.pt/", "B") };
        var rows = Rows(tabs, RadarScope.Duplicates);
        Assert.Equal(["A1", "A2"], Titles(rows));
        Assert.All(rows, row => Assert.Equal("2 com o mesmo endereço", row.Status));
    }

    [Fact]
    public void Scope_Duplicates_IgnoresFragmentsAndInternalPages()
    {
        var tabs = new[]
        {
            Tab("https://a.pt/", "A"), Tab("https://a.pt/#x", "A fragmento"),
            Tab("eteris://newtab/", "Nova 1"), Tab("eteris://newtab/", "Nova 2"),
        };
        Assert.Empty(Rows(tabs, RadarScope.Duplicates));
    }

    [Fact]
    public void Scope_Closed_ListsRecentlyClosedInGivenOrder()
    {
        var closed = new ClosedTab[] { new(1, "https://b.pt/", "B"), new(0, "https://a.pt/", "A") };
        var rows = Rows([Tab("https://c.pt/", "C")], RadarScope.Closed, RadarSort.Recent, closed: closed);
        Assert.Equal(["B", "A"], Titles(rows));
        Assert.All(rows, row =>
        {
            Assert.Null(row.Tab);
            Assert.NotNull(row.Closed);
            Assert.Equal("Fechada recentemente", row.Status);
        });
    }

    // ---------- Ordenações ----------

    [Fact]
    public void Sort_Recent_PutsActiveFirstThenMostRecentlyUsed()
    {
        var old = Tab("https://a.pt/", "Antiga", minutesAgo: 60);
        var recent = Tab("https://b.pt/", "Recente", minutesAgo: 1);
        var active = Tab("https://c.pt/", "Ativa", minutesAgo: 30);
        var rows = Rows([old, recent, active], sort: RadarSort.Recent, active: active);
        Assert.Equal(["Ativa", "Recente", "Antiga"], Titles(rows));
        Assert.Equal("Em uso", rows[0].Status);
    }

    [Fact]
    public void Sort_Site_OrdersByHostThenTitleIgnoringCase()
    {
        var tabs = new[]
        {
            Tab("https://zeta.pt/", "Z"), Tab("https://Alfa.pt/b", "beta"), Tab("https://alfa.pt/a", "Alfa"), Tab("eteris://history/", "Histórico"),
        };
        Assert.Equal(["Alfa", "beta", "Histórico", "Z"], Titles(Rows(tabs, sort: RadarSort.Site)));
    }

    [Fact]
    public void Sort_Strip_KeepsTabStripOrderEvenWithActiveTab()
    {
        var tabs = new[] { Tab("https://a.pt/", "A", 1), Tab("https://b.pt/", "B", 50), Tab("https://c.pt/", "C", 5) };
        Assert.Equal(["A", "B", "C"], Titles(Rows(tabs, sort: RadarSort.Strip, active: tabs[2])));
    }

    // ---------- Pesquisa ----------

    [Theory]
    [InlineData("WIKI", new[] { "Fado – Wikipédia" })]
    [InlineData("ipma", new[] { "Previsão do tempo" })]
    [InlineData("previsao", new[] { "Previsão do tempo" })]
    [InlineData("  fado  ", new[] { "Fado – Wikipédia" })]
    [InlineData("gitlab", new string[0])]
    public void Query_MatchesTitleOrHostIgnoringCaseAndAccents(string query, string[] expected)
    {
        var tabs = new[] { Tab("https://pt.wikipedia.org/wiki/Fado", "Fado – Wikipédia"), Tab("https://www.ipma.pt/", "Previsão do tempo") };
        Assert.Equal(expected, Titles(Rows(tabs, query: query)));
    }

    [Fact]
    public void Query_AlsoSearchesClosedTabs()
    {
        var closed = new ClosedTab[] { new(0, "https://a.pt/", "Alfa"), new(1, "https://b.pt/", "Beta") };
        Assert.Equal(["Beta"], Titles(Rows([], RadarScope.Closed, query: "beta", closed: closed)));
    }

    // ---------- Linhas ----------

    [Fact]
    public void Row_StatusCombinesStatesInOrder()
    {
        var tab = Tab("https://a.pt/", "A");
        tab.IsPinned = true;
        tab.IsSleeping = true;
        tab.IsMuted = true;
        tab.IsAudible = true;
        Assert.Equal("Em uso · Fixada · A dormir · Silenciada", TabRadar.FromTab(tab, active: true, duplicates: 0).Status);
        Assert.Equal(string.Empty, TabRadar.FromTab(Tab("https://b.pt/", "B"), active: false, duplicates: 0).Status);
    }

    [Fact]
    public void Row_UsesSiteWhenTitleIsBlank()
    {
        var row = TabRadar.FromTab(Tab("https://www.a.pt/x", "  "), active: false, duplicates: 0);
        Assert.Equal("www.a.pt", row.Title);
        Assert.Equal("www.a.pt", row.Subtitle);
    }

    [Theory]
    [InlineData("eteris://newtab/", "Éteris")]
    [InlineData("eteris://history/", "Éteris")]
    [InlineData("not a url", "Éteris")]
    [InlineData("https://www.publico.pt/artigo", "www.publico.pt")]
    public void Site_FallsBackToEterisForInternalPages(string address, string site)
        => Assert.Equal(site, TabRadar.Site(address));

    [Theory]
    [InlineData("https://a.pt", "https://a.pt/")]
    [InlineData("HTTPS://A.PT/x", "https://a.pt/x")]
    [InlineData("eteris://newtab/", null)]
    [InlineData("file:///C:/x.html", null)]
    public void WebAddress_NormalizesHttpOnly(string address, string? expected)
        => Assert.Equal(expected, TabRadar.WebAddress(address));

    // ---------- Texto de estado ----------

    [Theory]
    [InlineData(0, RadarScope.All, "", "Nenhuma aba neste filtro.")]
    [InlineData(0, RadarScope.Closed, "", "Ainda não fechou abas nesta janela.")]
    [InlineData(0, RadarScope.Closed, "x", "Sem resultados. Experimente outro termo ou filtro.")]
    [InlineData(0, RadarScope.All, "  x ", "Sem resultados. Experimente outro termo ou filtro.")]
    [InlineData(1, RadarScope.All, "", "1 aba nesta vista")]
    [InlineData(7, RadarScope.Pinned, "a", "7 abas nesta vista")]
    public void Status_DescribesCountOrWhyEmpty(int count, RadarScope scope, string query, string expected)
        => Assert.Equal(expected, TabRadar.Status(count, scope, query));
}
