using Eteris.Browser.Browser;
using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// eteris://vigias/ e os avisos das vigias: os textos de estado, o aviso de cada resultado (o preço com os separadores
// portugueses) e a página, que escapa os títulos das páginas vigiadas e expõe os últimos preços no <body>.
public class WatchesPageTests
{
    private static readonly DateTime Now = new(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Notice_PriceDropped_UsesPortugueseFormat()
        => Assert.Equal("O preço baixou: SSD Kingston — R$ 588,22 → R$ 499,99",
            WatchesPage.Notice(Watch(title: "SSD Kingston", lastPrice: 588.22m, currency: "BRL"), WatchOutcome.PriceDropped,
                new WatchCheck(Now, WatchStatus.Ok, 499.99m, "BRL", null, PageBlock.None)));

    [Fact] public void Notice_ReachedTarget_SaysTheNewPrice()
        => Assert.Equal("Chegou ao preço que queria: SSD — 1.199,00 €",
            WatchesPage.Notice(Watch(lastPrice: 1250m, currency: "EUR", target: 1200m), WatchOutcome.ReachedTarget,
                new WatchCheck(Now, WatchStatus.Ok, 1199m, "EUR", null, PageBlock.None)));

    [Fact] public void Notice_OtherOutcomes()
    {
        Assert.Equal("Mudou: SSD", WatchesPage.Notice(Watch(kind: WatchKind.Change), WatchOutcome.Changed, Check()));
        Assert.Equal("pichau.com.br não deixa vigiar sozinho: o preço passa a ser lido quando abrir a página",
            WatchesPage.Notice(Watch(), WatchOutcome.BecameBlocked, Check(WatchStatus.Blocked, PageBlock.Challenge)));
        Assert.Equal("A vigia de «SSD» perdeu o pedaço que vigiava",
            WatchesPage.Notice(Watch(kind: WatchKind.Change), WatchOutcome.PieceMissing, Check(WatchStatus.Missing)));
    }

    [Fact] public void Notice_Nothing_IsNull() => Assert.Null(WatchesPage.Notice(Watch(), WatchOutcome.Nothing, Check()));

    [Fact] public void Status_Texts()
    {
        Assert.Equal("à espera da 1.ª verificação", WatchesPage.Status(Watch(status: WatchStatus.New), Now));
        Assert.Equal("verificado há 20 min", WatchesPage.Status(Watch(status: WatchStatus.Ok, lastCheck: Now.AddMinutes(-20)), Now));
        Assert.Equal("verificado há 3 h", WatchesPage.Status(Watch(status: WatchStatus.Ok, lastCheck: Now.AddHours(-3)), Now));
        // A hora sai na hora local do PC (em Lisboa, no inverno, igual à UTC: «01/12 10:00»).
        Assert.Equal("falhou desde " + Now.AddHours(-2).ToLocalTime().ToString("dd/MM HH:mm"),
            WatchesPage.Status(Watch(status: WatchStatus.Failing, failingSince: Now.AddHours(-2)), Now));
        Assert.Equal("este site não deixa vigiar sozinho — o preço é lido quando abre a página", WatchesPage.Status(Watch(status: WatchStatus.Blocked), Now));
        Assert.Equal("o pedaço desapareceu", WatchesPage.Status(Watch(status: WatchStatus.Missing), Now));
        Assert.Equal("em pausa", WatchesPage.Status(Watch(status: WatchStatus.Ok, paused: true), Now));
    }

    // Uma verificação de há segundos não diz «há 0 min».
    [Fact] public void Status_JustChecked_SaysOneMinute()
        => Assert.Equal("verificado há 1 min", WatchesPage.Status(Watch(status: WatchStatus.Ok, lastCheck: Now.AddSeconds(-10)), Now));

    [Fact] public void Render_EscapesTitlesAndExposesLastPrices()
    {
        var html = WatchesPage.Render([Watch(title: "<script>x</script>", lastPrice: 1234.56m)], _ => [], Now);
        Assert.DoesNotContain("<script>x</script>", html);
        Assert.Contains("data-last-prices=\"1234.56\"", html);
    }

    // O teste ao vivo procura «999.90»: o preço sai sempre com pelo menos duas casas, em formato invariante.
    [Fact] public void Render_LastPricesKeepTwoDecimals()
    {
        var html = WatchesPage.Render([Watch(id: 1, lastPrice: 999.9m), Watch(id: 2, lastPrice: 1000m), Watch(id: 3, lastPrice: null)], _ => [], Now);
        Assert.Contains("data-last-prices=\"999.90 1000.00\"", html);
    }

    // A página privada não mostra as vigias do perfil nem leva o token das ações.
    [Fact] public void Render_Private_HasNoWatchesNorToken()
    {
        var html = WatchesPage.Render([], _ => [], Now, isPrivate: true);
        Assert.Contains("data-last-prices=\"\"", html);
        Assert.DoesNotContain(InternalPages.Token, html);
    }

    private static WatchCheck Check(WatchStatus status = WatchStatus.Ok, PageBlock block = PageBlock.None)
        => new(Now, status, null, null, null, block);

    private static PageWatch Watch(long id = 1, string title = "SSD", WatchKind kind = WatchKind.Price, decimal? lastPrice = null,
        string? currency = "BRL", decimal? target = null, WatchStatus status = WatchStatus.Ok, bool paused = false,
        DateTime? lastCheck = null, DateTime? failingSince = null)
        => new(id, "https://www.pichau.com.br/ssd", "pichau.com.br", title, kind, kind == WatchKind.Change ? "#estado" : null,
            UseSession: false, IntervalMinutes: 120, TargetPrice: target, Paused: paused, Status: status, Failures: 0,
            CreatedUtc: Now.AddDays(-1), LastCheckUtc: status == WatchStatus.New ? null : lastCheck ?? Now.AddHours(-1), NextCheckUtc: Now.AddHours(1),
            LastText: null, LastPrice: lastPrice, Currency: currency, FailingSinceUtc: failingSince, LastBlock: PageBlock.None);
}
