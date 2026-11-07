using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// «HTTPS primeiro» com aviso: quando o https:// falha, a página «Este site não oferece ligação segura» em vez do
// recuo silencioso, com «Continuar sem segurança (só desta vez)» e «Sempre para este site».
public class HttpsWarningTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "eteris-http-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    [Fact]
    public void Recovery_UpgradedSiteRefusingHttps_GetsTheWarningWithTheOriginalAddress()
    {
        var https = new HttpsFirst();
        var upgraded = https.Upgrade("http://velho.pt/a?b=1")!;
        var page = ErrorPage.Recovery(-200, upgraded, https.FailedUpgrade)!;
        Assert.True(ErrorPage.IsInsecureWarning(page));
        Assert.Equal("http://velho.pt/a?b=1", ErrorPage.FailedUrl(page));
        Assert.Equal(-200, ErrorPage.Code(page));
    }

    // Já escolheu continuar neste site: voltar atrás até à entrada https:// que falhou abre o http:// sem novo aviso.
    [Fact]
    public void Recovery_SiteAlreadyAllowed_GoesStraightToHttp()
    {
        var https = new HttpsFirst();
        var upgraded = https.Upgrade("http://velho.pt/a")!;
        https.Allow("velho.pt", isPrivate: false);
        Assert.Equal("http://velho.pt/a", ErrorPage.Recovery(-107, upgraded, https.FailedUpgrade, u => https.IsAllowedUrl(u, false)));
        // Na janela privada a escolha da normal não conta.
        Assert.True(ErrorPage.IsInsecureWarning(ErrorPage.Recovery(-107, upgraded, https.FailedUpgrade, u => https.IsAllowedUrl(u, true))!));
    }

    // O nome não existe: não é culpa do HTTPS; página de erro com o endereço que a pessoa abriu.
    [Fact]
    public void Recovery_UpgradedSiteThatDoesNotExist_ShowsTheErrorForTheHttpAddress()
    {
        var https = new HttpsFirst();
        var upgraded = https.Upgrade("http://nao-existe.pt/")!;
        Assert.Equal(ErrorPage.Url(-105, "http://nao-existe.pt/"), ErrorPage.Recovery(-105, upgraded, https.FailedUpgrade));
    }

    [Fact]
    public void Recovery_HttpsTypedByThePerson_IsAnOrdinaryError()
    {
        var https = new HttpsFirst();
        var page = ErrorPage.Recovery(-102, "https://recusa.pt/", https.FailedUpgrade)!;
        Assert.False(ErrorPage.IsInsecureWarning(page));
    }

    [Fact]
    public void InsecureWarning_OffersBothChoices_AndHidesAlwaysInPrivate()
    {
        var html = ErrorPage.Render(-102, "http://velho.pt/", insecure: true);
        Assert.Equal("Este site não oferece ligação segura", Data(html).GetProperty("title").GetString());
        Assert.Contains("\"insecure\":true", html);
        Assert.Contains("Continuar sem segurança (só desta vez)", html);
        Assert.Contains("Sempre para este site", html);
        Assert.Contains("always=1", html);
        var inPrivate = ErrorPage.Render(-102, "http://velho.pt/", insecure: true, canRemember: false);
        Assert.Contains("\"proceedAlways\":null", inPrivate);
        Assert.DoesNotContain("always=1", inPrivate);
    }

    private static System.Text.Json.JsonElement Data(string html)
    {
        var start = html.IndexOf("id=\"data\">", StringComparison.Ordinal) + "id=\"data\">".Length;
        return System.Text.Json.JsonDocument.Parse(html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)]).RootElement;
    }

    [Fact]
    public void Render_InsecureEscapesTheAddress()
    {
        var html = ErrorPage.Render(-102, "http://x.pt/\"</script><script>alert(1)</script>", insecure: true);
        Assert.DoesNotContain("<script>alert(1)", html);
    }

    [Fact]
    public void Continue_NeedsTheTokenAndAnHttpAddress()
    {
        var once = ErrorPage.ParseContinue(ErrorPage.ContinueUrl("http://velho.pt/a", always: false))!;
        Assert.Equal(new ErrorPage.InsecureChoice("http://velho.pt/a", "velho.pt", false), once);
        Assert.True(ErrorPage.ParseContinue(ErrorPage.ContinueUrl("http://velho.pt/", always: true))!.Always);
        // Sem token, com outro token, ou para outro esquema: recusado.
        Assert.Null(ErrorPage.ParseContinue("eteris://error/continue?url=http%3A%2F%2Fvelho.pt%2F"));
        Assert.Null(ErrorPage.ParseContinue("eteris://error/continue?url=http%3A%2F%2Fvelho.pt%2F&t=00"));
        Assert.Null(ErrorPage.ParseContinue(ErrorPage.ContinueUrl("javascript:alert(1)", always: false)));
        Assert.Null(ErrorPage.ParseContinue(ErrorPage.ContinueUrl("file:///c:/x", always: false)));
    }

    [Fact]
    public void ContinueRedirect_ReplacesTheWarningWithTheSite()
    {
        var html = ErrorPage.ContinueRedirect("http://x.pt/</script><script>alert(1)</script>");
        Assert.Contains("location.replace(", html);
        Assert.DoesNotContain("<script>alert(1)", html);
    }

    // As exceções guardam o host exato: aceitar http:// em velho.exemplo.pt não abre loja.exemplo.pt.
    [Fact]
    public void ExceptionStore_KeepsExactHosts()
    {
        var store = new StrictSiteStore(_db, "http_sites", exactHost: true);
        store.Add("Velho.Exemplo.pt");
        Assert.True(store.Contains("velho.exemplo.pt"));
        Assert.False(store.Contains("loja.exemplo.pt"));
        Assert.True(new StrictSiteStore(_db, "http_sites", exactHost: true).Contains("velho.exemplo.pt"));
        store.Remove("velho.exemplo.pt");
        Assert.Empty(store.All());
    }

    // O aviso continua a ser «página de erro» para a omnibox, o F5 e os painéis.
    [Fact]
    public void InsecureWarning_IsAnErrorPage()
    {
        var page = ErrorPage.InsecureUrl(-102, "http://velho.pt/");
        Assert.True(ErrorPage.IsErrorPage(page));
        Assert.False(ErrorPage.IsInsecureWarning(ErrorPage.Url(-102, "http://velho.pt/")));
    }
}
