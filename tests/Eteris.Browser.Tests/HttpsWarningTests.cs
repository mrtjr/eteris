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

    // Já escolheu continuar neste site e o https:// voltou a falhar: foi o próprio http:// que levou de volta ao https://
    // (301 do servidor ou HSTS). Página de erro do https://, sem «Continuar» — voltar ao http:// repetia o ciclo.
    [Fact]
    public void Recovery_SiteAlreadyAllowed_IsAnOrdinaryHttpsError_NotALoop()
    {
        var https = new HttpsFirst();
        var upgraded = https.Upgrade("http://loja.pt/")!;
        Assert.True(ErrorPage.IsInsecureWarning(ErrorPage.Recovery(-101, upgraded, u => https.FailedUpgrade(u, false), u => https.IsAllowedUrl(u, false))!));
        https.Allow("loja.pt", isPrivate: false);
        // «Continuar»: o http:// carrega, o servidor manda para https://loja.pt/, que falha outra vez (repetidamente).
        for (var i = 0; i < 3; i++)
            Assert.Equal(ErrorPage.Url(-101, upgraded), ErrorPage.Recovery(-101, upgraded, u => https.FailedUpgrade(u, false), u => https.IsAllowedUrl(u, false)));
        // Na janela privada a escolha da normal não conta.
        var inPrivate = https.Upgrade("http://loja.pt/", isPrivate: true)!;
        Assert.True(ErrorPage.IsInsecureWarning(ErrorPage.Recovery(-101, inPrivate, u => https.FailedUpgrade(u, true), u => https.IsAllowedUrl(u, true))!));
    }

    // O https:// que redireciona de volta para http:// (que o Éteris voltava a subir) até ERR_TOO_MANY_REDIRECTS: o site
    // não fala HTTPS — aviso com «Continuar», venha a desistência no endereço https:// ou no http://.
    [Fact]
    public void Recovery_RedirectLoopBackToHttp_GetsTheWarning()
    {
        var https = new HttpsFirst();
        var upgraded = https.Upgrade("http://portal.pt/")!;
        Assert.Equal(ErrorPage.InsecureUrl(-310, "http://portal.pt/"), ErrorPage.Recovery(-310, upgraded, https.FailedUpgrade));
        var other = new HttpsFirst();
        other.Upgrade("http://portal.pt/");
        Assert.Equal(ErrorPage.InsecureUrl(-310, "http://portal.pt/"), ErrorPage.Recovery(-310, "http://portal.pt/", other.FailedUpgrade));
        // Num http:// que nunca foi subido, é a página de erro de sempre.
        Assert.Equal(ErrorPage.Url(-310, "http://outro.pt/"), ErrorPage.Recovery(-310, "http://outro.pt/", https.FailedUpgrade));
        // Resposta vazia do https:// (a porta 443 aceita e não diz nada): também é o aviso.
        var empty = https.Upgrade("http://vazio.pt/")!;
        Assert.Equal(ErrorPage.InsecureUrl(-324, "http://vazio.pt/"), ErrorPage.Recovery(-324, empty, https.FailedUpgrade));
    }

    // Um https:// aberto à mão (um favorito) com o certificado falso, depois de o mesmo endereço ter sido subido e ter
    // funcionado: é a página de certificado, nunca o aviso com «Continuar sem segurança».
    [Fact]
    public void Recovery_HttpsOpenedLaterAfterAWorkingUpgrade_IsTheCertificateError()
    {
        var now = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
        var https = new HttpsFirst(clock: () => now);
        var upgraded = https.Upgrade("http://banco.example/")!;
        https.Settled(upgraded);
        Assert.Equal(ErrorPage.Url(-202, upgraded), ErrorPage.Recovery(-202, upgraded, https.FailedUpgrade));
        // Sem resposta nenhuma, a marca acaba por expirar.
        https.Upgrade("http://banco.example/");
        now += HttpsFirst.UpgradeWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(ErrorPage.Url(-202, upgraded), ErrorPage.Recovery(-202, upgraded, https.FailedUpgrade));
    }

    // «Voltar» (barra, Alt+←, botão do rato) a partir da página de erro, do aviso, da página «Continuar» ou do http://
    // aceite: a entrada que falhou é saltada. Ir para a frente, ou chegar ali de outra página, não.
    [Fact]
    public void SkipsBack_OnlyWhenStepingBackFromTheErrorOfThatAddress()
    {
        const string failed = "https://velho.pt/a";
        Assert.True(ErrorPage.SkipsBack(true, ErrorPage.InsecureUrl(-102, "http://velho.pt/a"), failed));
        Assert.True(ErrorPage.SkipsBack(true, ErrorPage.Url(-105, "https://velho.pt/a"), failed));
        Assert.True(ErrorPage.SkipsBack(true, ErrorPage.ContinueUrl("http://velho.pt/a", always: false), failed));
        Assert.True(ErrorPage.SkipsBack(true, "http://velho.pt/a", failed));
        Assert.False(ErrorPage.SkipsBack(false, ErrorPage.Url(-105, failed), failed));
        Assert.False(ErrorPage.SkipsBack(true, "https://outra.pt/", failed));
        Assert.False(ErrorPage.SkipsBack(true, ErrorPage.Url(-105, "https://outra.pt/"), failed));
        Assert.False(ErrorPage.SkipsBack(true, "https://velho.pt/a", failed));
        Assert.False(ErrorPage.SkipsBack(true, null, failed));
        // Depois de um salto, a entrada anterior com o mesmo endereço a falhar (o http:// aceite que voltou ao https://)
        // também se salta; sem esse salto antes, chegar ali vindo do próprio endereço não conta.
        Assert.True(ErrorPage.SkipsBack(true, "https://velho.pt/a", failed, afterSkip: true));
        Assert.False(ErrorPage.SkipsBack(true, "https://outra.pt/", failed, afterSkip: true));
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
