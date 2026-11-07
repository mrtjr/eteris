using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

// «Esquecer este site ao fechar» e «Triturar»: o âmbito é o domínio registável e os seus subdomínios — nunca
// outro site, nem um com nome parecido.
public sealed class ForgetSiteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-forget-" + Guid.NewGuid() + ".db");
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Theory]
    [InlineData("https://www.exemplo.pt/a", "exemplo.pt")]
    [InlineData("http://loja.exemplo.pt/", "exemplo.pt")]
    [InlineData("https://www.publico.com.br/x", "publico.com.br")]
    [InlineData("https://bbc.co.uk/", "bbc.co.uk")]
    [InlineData("http://192.168.1.10:8080/", "192.168.1.10")]
    public void SiteOf_IsTheRegistrableDomain(string url, string site) => Assert.Equal(site, SiteScope.SiteOf(url));

    [Theory]
    [InlineData("eteris://newtab/")]
    [InlineData("file:///C:/x.html")]
    [InlineData("about:blank")]
    [InlineData("não é endereço")]
    public void SiteOf_OnlyForWebPages(string url) => Assert.Null(SiteScope.SiteOf(url));

    [Theory]
    [InlineData("https://exemplo.pt/", true)]
    [InlineData("https://a.b.exemplo.pt/", true)]
    [InlineData("https://malexemplo.pt/", false)]
    [InlineData("https://exemplo.pt.evil.com/", false)]
    [InlineData("https://exemplo.com/", false)]
    public void Belongs_NeverReachesAnotherSite(string url, bool expected) => Assert.Equal(expected, SiteScope.Belongs(url, "exemplo.pt"));

    [Theory]
    [InlineData(".exemplo.pt", true)]
    [InlineData("www.exemplo.pt", true)]
    [InlineData("exemplo.pt", true)]
    [InlineData(".malexemplo.pt", false)]
    [InlineData("exemplo.pt.evil.com", false)]
    [InlineData("", false)]
    public void CookieBelongs_MatchesTheSiteAndSubdomains(string domain, bool expected)
        => Assert.Equal(expected, SiteScope.CookieBelongs(domain, "exemplo.pt"));

    [Fact]
    public void CookieUrl_IsHttpsOnTheCookieHost()
    {
        Assert.Equal("https://exemplo.pt/", SiteScope.CookieUrl(".exemplo.pt", "/"));
        Assert.Equal("https://www.exemplo.pt/conta", SiteScope.CookieUrl("www.exemplo.pt", "conta"));
    }

    [Fact]
    public void Origins_IncludeSeenSubdomainsAndPortsButNothingElse()
    {
        var origins = SiteScope.Origins("exemplo.pt", ["https://loja.exemplo.pt/x", "http://exemplo.pt:8080/", "https://outro.pt/"]);
        Assert.Contains("https://loja.exemplo.pt", origins);
        Assert.Contains("http://exemplo.pt:8080", origins);
        Assert.Contains("https://www.exemplo.pt", origins);
        Assert.Contains("http://exemplo.pt", origins);
        Assert.DoesNotContain(origins, o => o.Contains("outro.pt"));
    }

    // Só se esquece quando já não há nenhuma aba do site — numa janela, a dormir ou noutro Espaço.
    [Fact]
    public void StillOpen_CountsAnyTabOfTheSite()
    {
        Assert.True(SiteScope.StillOpen("exemplo.pt", ["https://pt.wikipedia.org/", "https://loja.exemplo.pt/"]));
        Assert.False(SiteScope.StillOpen("exemplo.pt", ["https://pt.wikipedia.org/", "https://malexemplo.pt/"]));
    }

    [Fact]
    public void ShredSummary_SaysWhatGoes()
    {
        var text = SiteScope.ShredSummary("exemplo.pt", 12, 1);
        Assert.Contains("12 cookies", text);
        Assert.Contains("1 entrada do histórico", text);
        Assert.Contains("exemplo.pt e dos seus subdomínios", text);
        Assert.Contains("Não dá para anular", text);
        Assert.Contains("nenhum cookie", SiteScope.ShredSummary("exemplo.pt", 0, 0));
        Assert.StartsWith("Vai apagar de 127.0.0.1:", SiteScope.ShredSummary("127.0.0.1", 1, 1));
    }

    // O histórico apaga só o site (e subdomínios): o resto fica.
    [Fact]
    public void History_CountsAndDeletesOnlyTheSite()
    {
        var store = new HistoryStore(_path);
        store.Record("https://www.exemplo.pt/a", "A", T0);
        store.Record("https://loja.exemplo.pt/b", "B", T0.AddMinutes(1));
        store.Record("https://malexemplo.pt/c", "C", T0.AddMinutes(2));
        store.Record("https://pt.wikipedia.org/wiki/Fado", "Fado", T0.AddMinutes(3));
        Assert.Equal(2, store.CountSite("exemplo.pt"));
        Assert.Equal(2, store.SiteUrls("exemplo.pt").Count);
        store.DeleteSite("exemplo.pt");
        Assert.Equal(0, store.CountSite("exemplo.pt"));
        Assert.Equal(["https://pt.wikipedia.org/wiki/Fado", "https://malexemplo.pt/c"], store.Visits(10).Select(v => v.Url));
    }

    // A lista «esquecer ao fechar» guarda o domínio registável: ligar em www.exemplo.pt vale para loja.exemplo.pt.
    [Fact]
    public void ForgetList_IsBySite()
    {
        var store = new StrictSiteStore(_path, "forget_sites");
        store.Add("www.exemplo.pt");
        Assert.True(store.Contains("loja.exemplo.pt"));
        Assert.False(store.Contains("malexemplo.pt"));
        Assert.Equal(["exemplo.pt"], store.All());
    }
}
