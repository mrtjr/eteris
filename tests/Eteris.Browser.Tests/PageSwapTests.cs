using Eteris.Browser.Browser;
using static Eteris.Browser.Browser.PageSwap;

namespace Eteris.Browser.Tests;

public class PageSwapTests
{
    [Fact]
    public void TabSwitch_KeepsTheLeavingPageUntilTheNewOnePresents()
    {
        // A (à vista) → B (escondida, Chromium): B mostra-se já (por baixo) e A fica por cima até B apresentar.
        var plan = Plan([(WasVisible: true, Visible: false, Arrives: true), (WasVisible: false, Visible: true, Arrives: true)]);
        Assert.Equal([Step.Hold, Step.Show], plan);
    }

    [Fact]
    public void NothingArriving_HidesAtOnce()
    {
        // Só esconder (ex.: a divisão do ecrã acabou e a outra metade já estava à vista): nada por que esperar.
        var plan = Plan([(true, false, true), (true, true, true), (false, false, true)]);
        Assert.Equal([Step.Hide, Step.Show, Step.Hide], plan);
    }

    [Fact]
    public void VideoEngineArriving_DoesNotHold()
    {
        // O WebView2 tem a sua própria troca sem intervalo (VideoEngine: espera pelo 1.º documento).
        var plan = Plan([(true, false, true), (false, true, false)]);
        Assert.Equal([Step.Hide, Step.Show], plan);
    }

    [Fact]
    public void PagesAlreadyHidden_StayHidden()
    {
        var plan = Plan([(false, false, true), (true, false, true), (false, true, true)]);
        Assert.Equal([Step.Hide, Step.Hold, Step.Show], plan);
    }

    [Fact]
    public void HoldIsBounded()
    {
        // Uma página que não responde nunca prende a antiga mais de 1 s.
        Assert.True(HoldLimit <= TimeSpan.FromSeconds(1));
        Assert.True(PresentMargin < TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void NextFrameScript_WaitsForTwoAnimationFrames()
    {
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(NextFrameScript, "requestAnimationFrame").Count);
        Assert.Contains("await", NextFrameScript);
    }

    [Fact]
    public void WarmPageComingBack_RisesAtOnceWithoutHolding()
    {
        // A (à vista) → W (quente: janela visível, tapada). W já está desenhada: sobe logo, A não fica a segurar.
        var plan = Plan([(WasVisible: true, Warm: false, Visible: false, Arrives: true), (WasVisible: true, Warm: true, Visible: true, Arrives: true)]);
        Assert.Equal([Step.Hide, Step.Show], plan);
    }

    [Fact]
    public void WarmPagesOutOfSight_StayAsTheyAre()
    {
        // Troca A → B (fria) com W quente: A segura, B entra, W fica tapada (é a regra da graça que a arrefece).
        var plan = Plan([(true, false, false, true), (false, false, true, true), (true, true, false, true)]);
        Assert.Equal([Step.Hold, Step.Show, Step.Keep], plan);
    }

    [Fact]
    public void WarmPageDoesNotCountAsArriving()
    {
        // Só quentes a mudar: nada a segurar.
        var plan = Plan([(true, true, true, true), (true, false, false, true), (false, false, false, true)]);
        Assert.Equal([Step.Show, Step.Hide, Step.Hide], plan);
    }

    [Fact]
    public void OldPlan_IsThePlanWithoutWarmPages()
    {
        (bool, bool, bool)[] pages = [(true, false, true), (false, true, true), (false, false, true), (true, true, false)];
        Assert.Equal(Plan(pages), Plan(pages.Select(p => (p.Item1, false, p.Item2, p.Item3)).ToList()));
    }

    [Fact]
    public void Stack_ShownThenLeavingThenArrivingThenWarm()
    {
        var order = StackOrder([
            ("quente antiga", Layer.Warm, -100L),
            ("a entrar", Layer.Arriving, 0L),
            ("quente recente", Layer.Warm, -200L),
            ("a sair (2.ª)", Layer.Held, 7L),
            ("à vista", Layer.Shown, 0L),
            ("a sair (1.ª)", Layer.Held, 3L),
        ]);
        Assert.Equal(["à vista", "a sair (1.ª)", "a sair (2.ª)", "a entrar", "quente recente", "quente antiga"], order);
    }

    [Fact]
    public void Stack_WhileHolding_TheLeavingPageCoversTheArrivingOne()
    {
        // Durante a troca: a que sai por cima da que entra (que se desenha por baixo); as quentes por baixo de tudo.
        var order = StackOrder([("entra", Layer.Arriving, 0L), ("quente", Layer.Warm, -1L), ("sai", Layer.Held, 1L)]);
        Assert.Equal(["sai", "entra", "quente"], order);
        // Depois de apresentar: a que entrou passa a «à vista» e sobe; a que saiu fica quente por baixo.
        order = StackOrder([("entrou", Layer.Shown, 0L), ("saiu", Layer.Warm, -2L), ("quente", Layer.Warm, -1L)]);
        Assert.Equal(["entrou", "saiu", "quente"], order);
    }

    [Fact]
    public void Stack_APageBeingHidden_GoesUnderEverything()
    {
        // A sai e não pode ficar quente (som, ecrã dividido) enquanto se volta à quente W: A esconde-se por baixo de W,
        // em vez de empatar com ela na camada «à vista».
        var order = StackOrder([("a esconder", Layer.Cooling, 0L), ("voltou", Layer.Shown, 0L), ("quente", Layer.Warm, -1L)]);
        Assert.Equal(["voltou", "quente", "a esconder"], order);
    }

    [Fact]
    public void Presentation_AColdPageIsOnlyWarmableAfterItPresents()
    {
        // Ctrl+Tab para B (fria) e de volta antes de B apresentar: B não pode ficar quente (voltaria sem espera,
        // com a superfície em branco ou antiga).
        var pages = new PagePresentation<string>();
        Assert.True(pages.HasPresented("b"));
        var arrival = pages.Arrive("b");
        Assert.False(pages.HasPresented("b"));
        Assert.Equal(arrival, pages.Pending("b"));
        Assert.True(pages.Presented("b", arrival));
        Assert.True(pages.HasPresented("b"));
        Assert.Null(pages.Pending("b"));
    }

    [Fact]
    public void Presentation_AnEarlierArrivalDoesNotConfirmTheCurrentOne()
    {
        // B saiu e voltou fria enquanto a espera da 1.ª chegada ainda corria: o rAF dessa não conta para a 2.ª.
        var pages = new PagePresentation<string>();
        var first = pages.Arrive("b");
        var second = pages.Arrive("b");
        Assert.False(pages.Presented("b", first));
        Assert.False(pages.HasPresented("b"));
        Assert.True(pages.Presented("b", second));
        Assert.True(pages.HasPresented("b"));
        Assert.False(pages.Presented("b", second));
    }

    [Fact]
    public void Presentation_ForgetDropsAClosedPage()
    {
        var pages = new PagePresentation<string>();
        var arrival = pages.Arrive("b");
        pages.Forget("b");
        Assert.True(pages.HasPresented("b"));
        Assert.False(pages.Presented("b", arrival));
    }

    // A espera de apresentação de uma página (até 1 s, mais 8 s para as lentas) é o único tempo em que o ajuste e8 não a
    // esconde. Quando a espera desiste (o 1.º documento ainda a carregar ao fim de 9 s), a página continua «por
    // apresentar» — não fica quente —, mas já ninguém espera por ela: pode esconder-se com a janela minimizada.
    [Fact]
    public void Waits_APageIsOnlyWaitingWhileAWaitRuns()
    {
        var waits = new PresentationWaits<string>();
        var pages = new PagePresentation<string>();
        Assert.False(waits.IsWaiting("b"));
        pages.Arrive("b");
        waits.Begin("b");
        Assert.True(waits.IsWaiting("b"));
        // A espera desistiu sem fotograma: acabou, e a página não passa por isso a «apresentada».
        waits.End("b");
        Assert.False(waits.IsWaiting("b"));
        Assert.False(pages.HasPresented("b"));
    }

    [Fact]
    public void Waits_OverlappingWaitsOfTheSamePageCountOneByOne()
    {
        // B saiu e voltou fria com a espera da 1.ª chegada ainda a correr: só deixa de estar à espera quando as duas acabam.
        var waits = new PresentationWaits<string>();
        waits.Begin("b");
        waits.Begin("b");
        waits.End("b");
        Assert.True(waits.IsWaiting("b"));
        waits.End("b");
        Assert.False(waits.IsWaiting("b"));
        // Acabar uma espera que não existe não deixa nada a dever à seguinte.
        waits.End("b");
        waits.Begin("b");
        Assert.True(waits.IsWaiting("b"));
    }

    [Fact]
    public void Waits_AreByPage()
    {
        var waits = new PresentationWaits<string>();
        waits.Begin("a");
        Assert.False(waits.IsWaiting("b"));
        waits.End("b");
        Assert.True(waits.IsWaiting("a"));
    }
}
