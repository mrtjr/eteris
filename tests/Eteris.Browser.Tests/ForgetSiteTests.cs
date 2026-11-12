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

    // Pela Public Suffix List, com a secção PRIVATE: vizinhos num sufixo partilhado são sites diferentes.
    [Theory]
    [InlineData("https://alice.github.io/x", "alice.github.io")]
    [InlineData("https://bob.github.io/", "bob.github.io")]
    [InlineData("https://foo.blogspot.com/", "foo.blogspot.com")]
    [InlineData("https://app.vercel.app/", "app.vercel.app")]
    [InlineData("https://www.saopaulo.sp.gov.br/", "saopaulo.sp.gov.br")]
    [InlineData("https://loja.exemplo.com.pt/", "exemplo.com.pt")]
    [InlineData("https://github.io/", "github.io")]
    [InlineData("http://localhost:3000/", "localhost")]
    [InlineData("http://[::1]:8080/", "[::1]")]
    public void SiteOf_UsesThePublicSuffixList(string url, string site) => Assert.Equal(site, SiteScope.SiteOf(url));

    [Fact]
    public void SharedSuffix_NeverReachesTheNeighbour()
    {
        Assert.False(SiteScope.Belongs("https://bob.github.io/", "alice.github.io"));
        Assert.False(SiteScope.CookieBelongs(".bob.github.io", "alice.github.io"));
        Assert.True(SiteScope.CookieBelongs(".alice.github.io", "alice.github.io"));
        // Triturar github.io (a página do próprio sufixo) não leva os cookies dos sites por baixo dele.
        Assert.False(SiteScope.CookieBelongs(".alice.github.io", "github.io"));
        Assert.False(SiteScope.StillOpen("alice.github.io", ["https://bob.github.io/"]));
    }

    // As chaves das permissões são host[:porta]: um subdomínio ou outra porta do site também são do site.
    [Theory]
    [InlineData("meet.google.com", "google.com", true)]
    [InlineData("google.com", "google.com", true)]
    [InlineData("app.exemplo.pt:8443", "exemplo.pt", true)]
    [InlineData("localhost:3000", "localhost", true)]
    [InlineData("[::1]:8080", "[::1]", true)]
    [InlineData("malexemplo.pt", "exemplo.pt", false)]
    [InlineData("bob.github.io", "alice.github.io", false)]
    [InlineData("", "exemplo.pt", false)]
    public void HostKeyBelongs_CoversSubdomainsAndPorts(string key, string site, bool expected)
        => Assert.Equal(expected, SiteScope.HostKeyBelongs(key, site));

    // Um IP ou localhost não tem «www.» (origem inválida para o motor, que abortava a limpeza das outras).
    [Fact]
    public void Origins_HaveNoWwwForAddressesAndLocalhost()
    {
        Assert.DoesNotContain(SiteScope.Origins("[::1]", ["http://[::1]:8080/"]), o => o.Contains("www."));
        Assert.DoesNotContain(SiteScope.Origins("127.0.0.1", ["https://127.0.0.1:5001/"]), o => o.Contains("www."));
        Assert.Contains("https://127.0.0.1:5001", SiteScope.Origins("127.0.0.1", ["https://127.0.0.1:5001/"]));
        Assert.DoesNotContain(SiteScope.Origins("localhost", ["http://localhost:3000/"]), o => o.Contains("www."));
    }

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
    public void ShredSummary_SaysTheDoneArchivedAndSnoozedTabsGo()
    {
        Assert.Contains("as abas feitas, arquivadas e adiadas deste site", SiteScope.ShredSummary("exemplo.pt", 1, 1));
        Assert.DoesNotContain("arquivadas", SiteScope.ShredSummary("exemplo.pt", 1, 1, isPrivate: true));
    }

    [Fact]
    public void Forget_ClearsEveryWindowAndTheClosedOnes()
    {
        var palette = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Palette.cs"));
        var body = palette[palette.IndexOf("private void Forget(int minutes)", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("ShowToast(", StringComparison.Ordinal)];
        Assert.Contains("NormalWindows()", body);
        Assert.Contains("s_closedWindows.Clear();", body);
        Assert.Contains("AppData.ForgetSince(", body);
        Assert.Contains("s_closedWindows.RemoveWhere(", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Undo.cs")));
        Assert.Contains("AppData.ForgetSiteTabs(site);", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Forget.cs")));
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

    // Com a regra do SiteScope (como a AppData a usa), ligar em alice.github.io não liga em bob.github.io.
    [Fact]
    public void ForgetList_UsesThePublicSuffixList()
    {
        var store = new StrictSiteStore(_path, "forget_sites", siteOf: SiteScope.OfHost);
        store.Add("alice.github.io");
        Assert.True(store.Contains("www.alice.github.io"));
        Assert.False(store.Contains("bob.github.io"));
        Assert.Equal(["alice.github.io"], store.All());
    }

    [Fact]
    public void CookieDeletions_TakeDomainAndSubdomainCookiesOfTheSite()
    {
        var plan = SiteScope.CookieDeletions(
            [(".exemplo.pt", "/", "sid"), ("accounts.exemplo.pt", "/login", "a"), ("www.exemplo.pt", "/", "h"), (".outro.pt", "/", "x"), (".malexemplo.pt", "/", "y")],
            "exemplo.pt");
        Assert.Equal(
            [("https://exemplo.pt/", "sid", "exemplo.pt"), ("https://accounts.exemplo.pt/login", "a", "accounts.exemplo.pt"), ("https://www.exemplo.pt/", "h", "www.exemplo.pt")],
            plan);
    }

    [Fact]
    public void SiteDataCleared_CountsTheCookies()
    {
        Assert.Equal("Dados de exemplo.pt apagados (3 cookies)", SiteScope.SiteDataCleared("exemplo.pt", 3, storage: true));
        Assert.Equal("Dados de exemplo.pt apagados (1 cookie)", SiteScope.SiteDataCleared("exemplo.pt", 1, storage: true));
        Assert.Equal("Dados de exemplo.pt apagados (nenhum cookie) — o armazenamento ficou por apagar", SiteScope.SiteDataCleared("exemplo.pt", 0, storage: false));
    }

    [Fact]
    public void LockPanel_ClearsTheWholeSite_LikeShred()
    {
        var panel = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.SiteInfo.cs"));
        Assert.DoesNotContain("DeleteCookiesAsync(info.Origin + \"/\")", panel);
        Assert.Contains("DeleteSiteCookiesAsync(", panel);
        Assert.Contains("SiteScope.SiteDataCleared(", panel);
        Assert.Contains("await DeleteSiteCookiesAsync(manager, site)", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Forget.cs")));
    }

    // Os cookies que o motor não deixou apagar e o perfil do motor de vídeo que não se abriu chegam ao aviso, e um site
    // de salto com alguma coisa por apagar fica à espera da volta seguinte; a contagem sem resposta não diz «nenhum».
    [Fact]
    public void SiteCleanup_ReportsTheCookiesAndTheVideoEngineThatStayed()
    {
        var forget = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Forget.cs"));
        Assert.Contains("catch (Exception) { cookiesFailed = true; }", forget);
        Assert.Contains("SiteScope.ShredDone(result.Cookies, result.History, result.Storage, IsPrivate, result.CookiesFailed, result.VideoEngineFailed)", forget);
        Assert.Contains("catch (Exception) { return -1; }", forget);
        var bounce = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Bounce.cs"));
        Assert.Contains("if (!done.Storage || done.CookiesFailed || done.VideoEngineFailed) continue;", bounce);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
