using Eteris.Browser.Watching;

namespace Eteris.Browser.Tests;

// As regras da vigia decidem o que uma verificação quer dizer: um desafio anti-robô ou uma página que não carregou nunca é
// «o preço mudou» nem «o pedaço desapareceu»; só um desafio (ou um salto para uma aplicação) duas vezes seguidas bloqueia;
// a primeira leitura nunca avisa; o preço avisa quando baixa e o alvo avisa uma vez, quando é atravessado.
public class WatchRulesTests
{
    private static readonly DateTime Now = new(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Normalize_CollapsesSpacesAndZeroWidth()
        => Assert.Equal("Em stock 3 unidades", WatchRules.Normalize("  Em stock\n\t3​ unidades "));

    [Fact] public void Normalize_NullIsEmpty() => Assert.Equal("", WatchRules.Normalize(null));

    [Theory]
    [InlineData(403, "https://chip7.pt/p", "https://chip7.pt/p", "Um momento…", false, PageBlock.Challenge)]
    [InlineData(503, "https://a.pt/p", "https://a.pt/p", "Just a moment...", false, PageBlock.Challenge)]
    [InlineData(200, "https://a.pt/p", "https://a.pt/p", "Produto", true, PageBlock.Challenge)]
    [InlineData(200, "https://produto.mercadolivre.com.br/MLB-1", "meli://items-core/MLB1", "", false, PageBlock.AppRedirect)]
    [InlineData(404, "https://a.pt/p", "https://a.pt/p", "Não encontrado", false, PageBlock.HttpError)]
    [InlineData(200, "https://a.pt/p", "https://a.pt/p", "Produto", false, PageBlock.None)]
    public void Classify_RecognisesBlocks(int status, string requested, string final, string title, bool marker, PageBlock expected)
        => Assert.Equal(expected, WatchRules.Classify(status, requested, final, title, marker));

    [Theory]
    [InlineData(403, "Acesso negado", PageBlock.HttpError)]                      // 403 sem sinal de desafio
    [InlineData(429, "Attention Required! | Cloudflare", PageBlock.Challenge)]
    [InlineData(200, "Just a moment...", PageBlock.None)]                        // título só conta com 403/429/503
    public void Classify_ChallengeTitleNeedsAChallengeCode(int status, string title, PageBlock expected)
        => Assert.Equal(expected, WatchRules.Classify(status, "https://a.pt/p", "https://a.pt/p", title, false));

    // Uma página de erro do motor (sem rede, nome que não resolve) não é «o site manda para uma aplicação».
    [Fact] public void Classify_EngineErrorPageIsALoadFailure()
        => Assert.Equal(PageBlock.HttpError, WatchRules.Classify(0, "https://a.pt/p", "chrome-error://chromewebdata/", "", false));

    // O motor da vigia cancela uma navegação para fora da web (meli://, um programa) e conta-a como salto para uma aplicação;
    // as páginas do próprio motor e os documentos locais não contam.
    [Theory]
    [InlineData("meli://items-core/MLB1", true)]
    [InlineData("mailto:loja@exemplo.pt", true)]
    [InlineData("eteris://definicoes/", true)]
    [InlineData("https://a.pt/p", false)]
    [InlineData("http://a.pt/p", false)]
    [InlineData("about:blank", false)]
    [InlineData("data:text/html,ola", false)]
    [InlineData("chrome-error://chromewebdata/", false)]
    [InlineData("não é um endereço", false)]
    public void IsAppScheme_OnlyOutsideTheWeb(string url, bool expected) => Assert.Equal(expected, WatchRules.IsAppScheme(url));

    // A espera do motor acaba quando há preço (numa vigia de preço) ou quando o pedaço tem texto (numa vigia de mudança);
    // um pedaço ainda vazio, ou só com o marcador do preço por pôr, não acaba a espera de uma vigia de preço.
    [Fact] public void Settled_PriceWatchWaitsForAPrice()
    {
        Assert.False(WatchRules.Settled(WatchKind.Price, Reading(piece: "—")));
        Assert.False(WatchRules.Settled(WatchKind.Price, Reading(piece: "")));
        Assert.True(WatchRules.Settled(WatchKind.Price, Reading(price: 1234.56m)));
    }

    [Fact] public void Settled_ChangeWatchWaitsForText()
    {
        Assert.False(WatchRules.Settled(WatchKind.Change, Reading(piece: "  ")));
        Assert.False(WatchRules.Settled(WatchKind.Change, Reading(pieceFound: false)));
        Assert.True(WatchRules.Settled(WatchKind.Change, Reading(piece: "Em stock")));
    }

    // Uma página de desafio também acaba a espera: esperar mais não a faz passar (o Éteris nunca contorna).
    [Fact] public void Settled_ChallengeEndsTheWait()
        => Assert.True(WatchRules.Settled(WatchKind.Price, Reading(piece: "", marker: true)));

    [Fact] public void Outcome_ChallengeIsNeverAChange()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 588.22m, status: WatchStatus.Ok);
        var check = WatchRules.ToCheck(w, Reading(price: null, pieceFound: false), PageBlock.Challenge, Now);
        Assert.Equal(WatchStatus.Failing, check.Status);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, check));
    }

    [Fact] public void SecondChallengeInARow_Blocks()
    {
        var w = Watch(status: WatchStatus.Failing, failures: 1, lastBlock: PageBlock.Challenge);
        var check = WatchRules.ToCheck(w, null, PageBlock.Challenge, Now);
        Assert.Equal(WatchStatus.Blocked, check.Status);
        Assert.Equal(WatchOutcome.BecameBlocked, WatchRules.Outcome(w, check));
    }

    [Fact] public void SecondAppRedirectInARow_Blocks()
    {
        var w = Watch(status: WatchStatus.Failing, failures: 1, lastBlock: PageBlock.AppRedirect);
        Assert.Equal(WatchStatus.Blocked, WatchRules.ToCheck(w, null, PageBlock.AppRedirect, Now).Status);
    }

    [Fact] public void ChallengeMarkerInTheReading_CountsAsAChallenge()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 588.22m, status: WatchStatus.Ok);
        var check = WatchRules.ToCheck(w, Reading(price: 1m, marker: true), PageBlock.None, Now);
        Assert.Equal((WatchStatus.Failing, PageBlock.Challenge, (decimal?)null), (check.Status, check.Block, check.Price));
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, check));
    }

    // Sem rede, tempo esgotado ou página vazia: falha passageira. Nunca bloqueia nem dá «o pedaço desapareceu» — duas
    // rondas sem rede não podem dizer que todos os sites recusam visitas.
    [Fact] public void LoadFailures_AreToleratedAndNeverBlock()
    {
        var w = Watch(kind: WatchKind.Change, lastText: "Esgotado", status: WatchStatus.Ok);
        var first = WatchRules.ToCheck(w, null, PageBlock.None, Now);
        Assert.Equal((WatchStatus.Ok, PageBlock.HttpError), (first.Status, first.Block));
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, first));

        var w2 = w with { Failures = 1, LastBlock = PageBlock.HttpError };
        Assert.Equal(WatchStatus.Ok, WatchRules.ToCheck(w2, Reading(pieceFound: false, title: ""), PageBlock.None, Now).Status);

        var w3 = w with { Failures = 2, LastBlock = PageBlock.HttpError };
        var third = WatchRules.ToCheck(w3, null, PageBlock.HttpError, Now);
        Assert.Equal(WatchStatus.Failing, third.Status);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w3, third));

        var w9 = w with { Status = WatchStatus.Failing, Failures = 9, LastBlock = PageBlock.HttpError };
        Assert.Equal(WatchStatus.Failing, WatchRules.ToCheck(w9, null, PageBlock.HttpError, Now).Status);
    }

    [Fact] public void IsFailure_CountsBlocksAndBadStatuses()
    {
        Assert.False(WatchRules.IsFailure(new WatchCheck(Now, WatchStatus.Ok, 1m, null, null, PageBlock.None)));
        Assert.True(WatchRules.IsFailure(new WatchCheck(Now, WatchStatus.Ok, null, null, null, PageBlock.HttpError)));
        Assert.True(WatchRules.IsFailure(new WatchCheck(Now, WatchStatus.Failing, null, null, null, PageBlock.None)));
        Assert.True(WatchRules.IsFailure(new WatchCheck(Now, WatchStatus.Missing, null, null, null, PageBlock.None)));
    }

    [Fact] public void PriceDrop_IsReported_AndRiseIsNot()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 588.22m, status: WatchStatus.Ok);
        Assert.Equal(WatchOutcome.PriceDropped, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(price: 499.99m), PageBlock.None, Now)));
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(price: 600m), PageBlock.None, Now)));
    }

    [Fact] public void PriceInADifferentCurrency_IsNotADrop()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 588.22m, currency: "BRL", status: WatchStatus.Ok);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(price: 99m, currency: "EUR"), PageBlock.None, Now)));
    }

    // Sem preço estruturado, o preço lê-se do pedaço apontado.
    [Fact] public void PriceWatch_ReadsThePieceWhenThereIsNoStructuredPrice()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 600m, currency: "BRL", status: WatchStatus.Ok);
        var check = WatchRules.ToCheck(w, Reading(price: null, piece: "R$ 1.234,56"), PageBlock.None, Now);
        Assert.Equal((WatchStatus.Ok, (decimal?)1234.56m, "BRL"), (check.Status, check.Price, check.Currency));
    }

    [Fact] public void Target_IsReportedOnceWhenCrossed()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 600m, target: 550m, status: WatchStatus.Ok);
        Assert.Equal(WatchOutcome.ReachedTarget, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(price: 549m), PageBlock.None, Now)));

        var below = w with { LastPrice = 549m };
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(below, WatchRules.ToCheck(below, Reading(price: 549m), PageBlock.None, Now)));
        Assert.Equal(WatchOutcome.PriceDropped, WatchRules.Outcome(below, WatchRules.ToCheck(below, Reading(price: 520m), PageBlock.None, Now)));
    }

    [Fact] public void Target_DropThatDoesNotReachIt_IsNotReported()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 600m, target: 550m, status: WatchStatus.Ok);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(price: 580m), PageBlock.None, Now)));
    }

    [Fact] public void FirstReading_IsNeverAChange()
    {
        var w = Watch(kind: WatchKind.Change, lastText: null, status: WatchStatus.New);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(piece: "Esgotado"), PageBlock.None, Now)));

        var p = Watch(kind: WatchKind.Price, lastPrice: null, target: 550m, status: WatchStatus.New);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(p, WatchRules.ToCheck(p, Reading(price: 500m), PageBlock.None, Now)));
    }

    [Fact] public void ChangedPiece_IsReported_WhitespaceIsNot()
    {
        var w = Watch(kind: WatchKind.Change, lastText: "Esgotado", status: WatchStatus.Ok);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(piece: " Esgotado\n"), PageBlock.None, Now)));
        Assert.Equal(WatchOutcome.Changed, WatchRules.Outcome(w, WatchRules.ToCheck(w, Reading(piece: "Em stock"), PageBlock.None, Now)));
    }

    [Fact] public void MissingPieceTwice_IsReportedOnce()
    {
        var w = Watch(kind: WatchKind.Change, lastText: "Esgotado", status: WatchStatus.Ok);
        var first = WatchRules.ToCheck(w, Reading(pieceFound: false), PageBlock.None, Now);
        Assert.Equal(WatchStatus.Failing, first.Status);
        var w2 = w with { Status = WatchStatus.Failing, Failures = 1 };
        var second = WatchRules.ToCheck(w2, Reading(pieceFound: false), PageBlock.None, Now);
        Assert.Equal(WatchStatus.Missing, second.Status);
        Assert.Equal(WatchOutcome.PieceMissing, WatchRules.Outcome(w2, second));
        var w3 = w with { Status = WatchStatus.Missing, Failures = 2 };
        var third = WatchRules.ToCheck(w3, Reading(pieceFound: false), PageBlock.None, Now);
        Assert.Equal(WatchStatus.Missing, third.Status);
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w3, third));
    }

    // «Desapareceu» só sai com uma leitura boa: uma falha da leitura passiva não a devolve à agenda, senão a próxima falta
    // avisava outra vez.
    [Fact] public void Missing_StaysMissingOnAnyFailure()
    {
        var w = Watch(kind: WatchKind.Change, lastText: "Esgotado", status: WatchStatus.Missing, failures: 2);
        var cases = new[]
        {
            (w, WatchRules.ToCheck(w, Reading(pieceFound: false), PageBlock.Challenge, Now)),
            (w, WatchRules.ToCheck(w, null, PageBlock.None, Now)),
            (w with { LastBlock = PageBlock.HttpError }, WatchRules.ToCheck(w with { LastBlock = PageBlock.HttpError }, Reading(pieceFound: false), PageBlock.None, Now)),
            (w with { LastBlock = PageBlock.Challenge }, WatchRules.ToCheck(w with { LastBlock = PageBlock.Challenge }, null, PageBlock.Challenge, Now)),
        };
        foreach (var (before, check) in cases)
        {
            Assert.Equal(WatchStatus.Missing, check.Status);
            Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(before, check));
        }
    }

    // Numa vigia de preço, uma página que carregou sem preço (esgotado, desenho novo) é uma falta como a do pedaço.
    [Fact] public void PriceWatch_PriceGoneTwice_IsMissing()
    {
        var w = Watch(kind: WatchKind.Price, lastPrice: 588.22m, status: WatchStatus.Ok);
        var first = WatchRules.ToCheck(w, Reading(price: null, pieceFound: false), PageBlock.None, Now);
        Assert.Equal((WatchStatus.Failing, PageBlock.None), (first.Status, first.Block));
        Assert.Equal(WatchOutcome.Nothing, WatchRules.Outcome(w, first));
        var w2 = w with { Status = WatchStatus.Failing, Failures = 1 };
        var second = WatchRules.ToCheck(w2, Reading(price: null, pieceFound: false), PageBlock.None, Now);
        Assert.Equal(WatchStatus.Missing, second.Status);
        Assert.Equal(WatchOutcome.PieceMissing, WatchRules.Outcome(w2, second));
    }

    [Fact] public void ChallengeThenMissingPiece_IsOnlyAFirstMiss()
    {
        var w = Watch(kind: WatchKind.Change, lastText: "Esgotado", status: WatchStatus.Failing, failures: 1, lastBlock: PageBlock.Challenge);
        Assert.Equal(WatchStatus.Failing, WatchRules.ToCheck(w, Reading(pieceFound: false), PageBlock.None, Now).Status);
    }

    // A leitura passiva (o utilizador abriu a página) grava o preço de uma vigia bloqueada, que continua bloqueada, e
    // avisa se baixou; um pedaço que voltou tira a vigia de «desapareceu».
    [Fact] public void PassiveReading_KeepsBlocked_AndBringsMissingBack()
    {
        var blocked = Watch(kind: WatchKind.Price, lastPrice: 588.22m, status: WatchStatus.Blocked, failures: 2, lastBlock: PageBlock.Challenge);
        var check = WatchRules.ToCheck(blocked, Reading(price: 499.99m), PageBlock.None, Now);
        Assert.Equal((WatchStatus.Blocked, (decimal?)499.99m), (check.Status, check.Price));
        Assert.Equal(WatchOutcome.PriceDropped, WatchRules.Outcome(blocked, check));

        var missing = Watch(kind: WatchKind.Change, lastText: "Esgotado", status: WatchStatus.Missing, failures: 2);
        var back = WatchRules.ToCheck(missing, Reading(piece: "Em stock"), PageBlock.None, Now);
        Assert.Equal(WatchStatus.Ok, back.Status);
        Assert.Equal(WatchOutcome.Changed, WatchRules.Outcome(missing, back));
    }

    private static PageWatch Watch(WatchKind kind = WatchKind.Price, decimal? lastPrice = null, string? currency = "BRL",
        WatchStatus status = WatchStatus.Ok, int failures = 0, PageBlock lastBlock = PageBlock.None, decimal? target = null,
        string? lastText = null)
        => new(1, "https://www.pichau.com.br/ssd", "pichau.com.br", "SSD", kind, kind == WatchKind.Change ? "#estado" : null,
            UseSession: false, IntervalMinutes: 120, TargetPrice: target, Paused: false, Status: status, Failures: failures,
            CreatedUtc: Now.AddDays(-1), LastCheckUtc: Now.AddHours(-2), NextCheckUtc: Now, LastText: lastText,
            LastPrice: lastPrice, Currency: currency, FailingSinceUtc: null, LastBlock: lastBlock);

    private static PageReading Reading(decimal? price = null, string? currency = "BRL", string? piece = null, bool pieceFound = true,
        string title = "SSD", bool marker = false)
        => new(price is { } p ? new PriceReading(p, currency, "ld") : null, pieceFound ? piece ?? "" : null, pieceFound, title,
            "https://www.pichau.com.br/ssd", marker);
}
