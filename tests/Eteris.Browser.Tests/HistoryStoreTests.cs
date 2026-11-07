using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-hist-" + Guid.NewGuid() + ".db");
    private readonly HistoryStore _store;
    private static readonly DateTime T0 = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public HistoryStoreTests() => _store = new HistoryStore(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Search_FindsByUrlFragment()
    {
        _store.Record("https://github.com/anthropics", "Anthropic", T0);
        var hit = Assert.Single(_store.Search("anthrop", 8));
        Assert.Equal("https://github.com/anthropics", hit.Url);
        Assert.Equal("Anthropic", hit.Title);
    }

    [Fact]
    public void Search_FindsByTitleCaseInsensitive()
    {
        _store.Record("https://x.pt/1", "Receita de Bacalhau", T0);
        Assert.Single(_store.Search("bacalhau", 8));
    }

    [Fact]
    public void Search_RanksHostPrefixBeforeOtherMatches()
    {
        _store.Record("https://example.com/about-github", "Sobre", T0);
        _store.Record("https://example.com/about-github", "Sobre", T0.AddMinutes(1));
        _store.Record("https://github.com/", "GitHub", T0);
        Assert.Equal("https://github.com/", _store.Search("git", 8)[0].Url);
    }

    [Fact]
    public void Search_RanksMoreVisitedFirstAmongEqualMatches()
    {
        _store.Record("https://a.pt/docs", "Docs A", T0);
        _store.Record("https://b.pt/docs", "Docs B", T0);
        _store.Record("https://b.pt/docs", "Docs B", T0.AddMinutes(1));
        Assert.Equal("https://b.pt/docs", _store.Search("docs", 8)[0].Url);
    }

    [Fact]
    public void Search_TreatsLikeWildcardsLiterally()
    {
        _store.Record("https://a.pt/x", "abc", T0);
        Assert.Empty(_store.Search("%", 8));
        Assert.Empty(_store.Search("_", 8));
    }

    [Fact]
    public void UpdateTitle_ChangesTitleOfLatestVisit()
    {
        _store.Record("https://a.pt/", "a.pt", T0);
        _store.UpdateTitle("https://a.pt/", "Página A");
        Assert.Equal("Página A", _store.Search("a.pt", 8)[0].Title);
    }

    [Fact]
    public void TopSites_GroupsByHostAndOrdersByVisits()
    {
        _store.Record("https://github.com/a", "A", T0);
        _store.Record("https://github.com/b", "B", T0.AddMinutes(1));
        _store.Record("https://www.github.com/c", "C", T0.AddMinutes(2));
        _store.Record("https://pt.wikipedia.org/", "Wikipédia", T0);
        var top = _store.TopSites(8);
        Assert.Equal(2, top.Count);
        Assert.Equal("github.com", top[0].Host);
        Assert.Equal(3, top[0].Visits);
        Assert.Equal("https://www.github.com/c", top[0].Url);
    }

    [Fact]
    public void Record_IgnoresNonWebSchemes()
    {
        _store.Record("eteris://newtab", "Nova aba", T0);
        _store.Record("about:blank", "", T0);
        Assert.Empty(_store.TopSites(8));
    }

    [Fact]
    public void SiteIcons_RequireAVisitAndDisappearWhenTheLastVisitIsForgotten()
    {
        const string address = "https://example.test/one";
        const string sibling = "https://example.test/two";
        const string otherPort = "https://example.test:8443/";
        const string encoded = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aV1sAAAAASUVORK5CYII=";
        var png = Convert.FromBase64String(encoded);

        _store.SaveSiteIcon(address, png);
        Assert.Empty(_store.SiteIcons([address]));

        _store.Record(address, "Uma página", T0);
        _store.Record(sibling, "Outra página", T0.AddMinutes(1));
        _store.SaveSiteIcon(address, png);
        var reopened = new HistoryStore(_path);
        Assert.Equal("data:image/png;base64," + encoded, Assert.Single(reopened.SiteIcons([sibling, otherPort])).Value);

        _store.DeleteVisit(_store.Visits(10).Single(v => v.Url == address).Id);
        Assert.Single(_store.SiteIcons([sibling]));
        _store.DeleteSince(T0.AddMinutes(1));
        Assert.Empty(_store.SiteIcons([sibling]));
    }

    [Fact]
    public void SiteIcon_RejectsNonPngOversizedAndNonWeb()
    {
        const string address = "https://example.test/";
        _store.Record(address, "Exemplo", T0);

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46 };
        _store.SaveSiteIcon(address, jpeg);
        Assert.Empty(_store.SiteIcons([address]));

        var oversized = new byte[8193];
        OnePixelPng.CopyTo(oversized, 0);
        _store.SaveSiteIcon(address, oversized);
        Assert.Empty(_store.SiteIcons([address]));

        var limit = new byte[8192];
        OnePixelPng.CopyTo(limit, 0);
        _store.SaveSiteIcon(address, limit);
        Assert.Single(_store.SiteIcons([address]));

        _store.SaveSiteIcon("eteris://newtab/", OnePixelPng);
        _store.SaveSiteIcon("file:///C:/x.png", OnePixelPng);
        Assert.Empty(_store.SiteIcons(["eteris://newtab/", "file:///C:/x.png"]));
        Assert.Equal(1, CountRows("site_icons"));
    }

    [Fact]
    public void SiteIcon_UpdatesWhenTheSiteChangesItsIcon()
    {
        const string address = "https://example.test/";
        var second = (byte[])OnePixelPng.Clone();
        second[^1] ^= 0xFF;
        _store.Record(address, "Exemplo", T0);

        _store.SaveSiteIcon(address, OnePixelPng);
        Assert.Equal(DataUrl(OnePixelPng), Assert.Single(_store.SiteIcons([address])).Value);

        _store.SaveSiteIcon(address, second);
        Assert.Equal(DataUrl(second), Assert.Single(_store.SiteIcons([address])).Value);
        Assert.Equal(1, CountRows("site_icons"));
    }

    [Fact]
    public void SiteIconKey_NormalisesHostCaseAndDefaultPort()
    {
        const string address = "https://github.com/anthropics";
        _store.Record(address, "GitHub", T0);
        _store.SaveSiteIcon(address, OnePixelPng);

        var icons = _store.SiteIcons(["https://GitHub.com/", "https://github.com:443/x", "HTTPS://github.com"]);
        Assert.Equal("https://github.com", Assert.Single(icons).Key);
        Assert.Empty(_store.SiteIcons(["http://github.com/", "https://github.com:8443/", "https://gist.github.com/"]));
    }

    [Fact]
    public void DeleteVisit_OfTheLastVisit_RemovesTheIcon()
    {
        const string address = "https://example.test/only";
        _store.Record(address, "Única", T0);
        _store.SaveSiteIcon(address, OnePixelPng);
        Assert.Single(_store.SiteIcons([address]));

        _store.DeleteVisit(Assert.Single(_store.Visits(10)).Id);
        Assert.Empty(_store.Visits(10));
        Assert.Empty(_store.SiteIcons([address]));
        Assert.Equal(0, CountRows("site_icons"));
    }

    [Fact]
    public void DeleteVisit_OfOneVisit_KeepsTheIconWhileAnotherRemains()
    {
        const string first = "https://example.test/";
        const string second = "https://example.test/two";
        _store.Record(first, "Um", T0);
        _store.Record(second, "Dois", T0.AddMinutes(1));
        _store.Record("https://other.test/", "Outro", T0.AddMinutes(2));
        _store.SaveSiteIcon(first, OnePixelPng);
        _store.SaveSiteIcon("https://other.test/", OnePixelPng);

        _store.DeleteVisit(_store.Visits(10).Single(v => v.Url == second).Id);
        Assert.Single(_store.SiteIcons([first]));

        _store.DeleteVisit(_store.Visits(10).Single(v => v.Url == first).Id);
        Assert.Empty(_store.SiteIcons([first]));
        Assert.Single(_store.SiteIcons(["https://other.test/"]));
    }

    [Fact]
    public void SiteIcons_HandlesMoreThanOneBatch()
    {
        var urls = Enumerable.Range(0, 300).Select(i => $"https://site{i:000}.test/page").ToArray();
        foreach (var url in urls)
        {
            _store.Record(url, "", T0);
            _store.SaveSiteIcon(url, OnePixelPng);
        }

        var icons = _store.SiteIcons(urls);
        Assert.Equal(300, icons.Count);
        Assert.True(icons.ContainsKey("https://site000.test"));
        Assert.True(icons.ContainsKey("https://site299.test"));
    }

    // Apagar visitas e «esquecer desde» correm na thread de I/O do CEF e na de UI: a limpeza dos
    // ícones tem de procurar as visitas do site pelo índice do endereço, não percorrer o histórico.
    [Fact]
    public void IconCleanup_ProbesHistoryByUrlIndexInsteadOfScanningIt()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + _path);
        connection.Open();
        connection.CreateFunction<string, string?>("site_icon_key", (string url) => url, isDeterministic: true);
        foreach (var sql in new[] { HistoryStore.DeleteIconOfLastVisitSql, HistoryStore.DeleteOrphanIconsSql })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            if (sql.Contains("$i")) command.Parameters.AddWithValue("$i", 1L);
            var plan = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) plan.Add(reader.GetString(3));

            Assert.Contains(plan, step => step.Contains("ix_history_url"));
            Assert.DoesNotContain(plan, step => step.StartsWith("SCAN") && step.Contains("history"));
        }
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aV1sAAAAASUVORK5CYII=");

    private static string DataUrl(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    private int CountRows(string table)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + _path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // Credenciais embutidas no endereço nunca entram no histórico (nem a senha de uma autenticação básica).
    [Fact]
    public void Record_DropsEmbeddedCredentials()
    {
        _store.Record("https://utilizador:senha@a.pt/privado?x=1", "Privado", T0);
        var visit = Assert.Single(_store.Visits(10));
        Assert.Equal("https://a.pt/privado?x=1", visit.Url);
        Assert.Equal("https://a.pt/privado?x=1", HistoryStore.WithoutUserInfo("https://utilizador:senha@a.pt/privado?x=1"));
        Assert.Equal("https://a.pt/x", HistoryStore.WithoutUserInfo("https://a.pt/x"));
        Assert.Equal("não é url", HistoryStore.WithoutUserInfo("não é url"));
    }
}
