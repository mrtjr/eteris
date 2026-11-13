using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// A Pausa (Ctrl+.): o que se cala, o que se pausa e o que volta (Tabs/PausePlan.cs). Os motores são as abas com motor de
// todos os Espaços, os motores de vídeo e os sites fixados na barra lateral; aqui são só nomes.
public class PausePlanTests
{
    private static PauseEngine<string> E(string name, bool muted = false, MediaState media = MediaState.None) => new(name, muted, media);

    [Fact]
    public void PlayingAudible_IsOnlyWhatPlaysWithSound()
    {
        var plan = PausePlan<string>.From([
            E("a", media: MediaState.Playing),
            E("b", muted: true, media: MediaState.Playing),
            E("c", media: MediaState.Paused),
            E("d"),
        ]);
        Assert.Equal(["a"], plan.PlayingAudible);
        // O script de marcar corre em todos: só mexe no que está a tocar.
        Assert.Equal(["a", "b", "c", "d"], plan.Engines);
    }

    [Fact]
    public void MutedByPause_IsEveryEngineNotAlreadyMuted()
    {
        var plan = PausePlan<string>.From([E("a", media: MediaState.Playing), E("b", muted: true, media: MediaState.Playing), E("c")]);
        Assert.Equal(["a", "c"], plan.MutedByPause);
    }

    [Fact]
    public void ATabMutedBeforeThePause_StaysMutedAfter()
    {
        var plan = PausePlan<string>.From([E("a", media: MediaState.Playing), E("b", muted: true, media: MediaState.Playing)]);
        var back = plan.Return([E("a"), E("b", muted: true)]);
        Assert.Equal(["a"], back.Unmute);
        Assert.DoesNotContain("b", back.Unmute);
        Assert.DoesNotContain("b", back.Replay);
    }

    [Fact]
    public void ATabMutedByHandDuringThePause_IsNotRestored()
    {
        var plan = PausePlan<string>.From([E("a", media: MediaState.Playing), E("c")]);
        var back = plan.Return([E("a", muted: true), E("c")]);
        Assert.Equal(["c"], back.Unmute);
    }

    [Fact]
    public void AClosedTab_LeavesThePlan()
    {
        var plan = PausePlan<string>.From([E("a", media: MediaState.Playing), E("b", media: MediaState.Playing), E("c")]);
        plan.Forget("a");
        Assert.Equal(["b"], plan.PlayingAudible);
        Assert.Equal(["b", "c"], plan.MutedByPause);
        Assert.Equal(["b", "c"], plan.Engines);
        // E uma que fechou sem aviso (não está entre os motores de agora) também não volta.
        var back = plan.Return([E("c")]);
        Assert.Equal(["c"], back.Unmute);
        Assert.Empty(back.Replay);
        Assert.False(back.OffersReplay);
    }

    [Fact]
    public void AnEmptyPlan_OffersNoResume()
    {
        var plan = PausePlan<string>.From([E("a", media: MediaState.Paused), E("b")]);
        var back = plan.Return([E("a"), E("b")]);
        Assert.False(back.OffersReplay);
        Assert.Empty(back.Replay);
        Assert.False(PausePlan<string>.From([]).Return([]).OffersReplay);
    }

    [Fact]
    public void Replay_IsExactlyThePlayingOnes()
    {
        var plan = PausePlan<string>.From([E("a", media: MediaState.Playing), E("b", media: MediaState.Paused), E("c", media: MediaState.Playing)]);
        var back = plan.Return([E("a"), E("b"), E("c")]);
        Assert.Equal(["a", "c"], back.Replay);
        Assert.True(back.OffersReplay);
    }

    // A sondagem da multimédia de uma aba de fundo pode ter até 30 s (Tabs/MediaPolling.cs), e um site fixado nem é
    // sondado: o que o script de marcar pausou conta como «a tocar» — se o motor não estava calado.
    [Fact]
    public void WhatTheMarkScriptPaused_CountsAsPlaying_UnlessMuted()
    {
        var plan = PausePlan<string>.From([E("a"), E("b", muted: true), E("c", media: MediaState.Playing), E("d")]);
        plan.Marked("a", 2);
        plan.Marked("b", 1);
        plan.Marked("c", 1);
        plan.Marked("d", 0);
        plan.Marked("x", 3);
        Assert.Equal(["a", "c"], plan.PlayingAudible.Order());
    }

    // Um motor que nasce durante a Pausa (uma aba aberta por um site, um site fixado que volta a carregar) entra calado,
    // e volta a ter som no regresso.
    [Fact]
    public void AnEngineBornDuringThePause_IsMutedAndRestored()
    {
        var plan = PausePlan<string>.From([E("a")]);
        plan.Add(E("n"));
        plan.Add(E("m", muted: true));
        plan.Add(E("a"));
        Assert.Equal(["a", "n"], plan.MutedByPause);
        Assert.Equal(["a", "n"], plan.Return([E("a"), E("n"), E("m", muted: true)]).Unmute);
    }

    // O que a Pausa parou não adormece enquanto ela dura (MainWindow.Sleep.cs): sem o «a tocar», a aba de fundo com música
    // adormecia no minuto seguinte, saía do «Retomar» e perdia o ponto onde ia. Segura-se o que tocava com som (pela
    // sondagem ou pelo script de marcar); o que sai do plano deixa de ser segurado.
    [Fact]
    public void WhatPlayed_IsHeldAwake_UntilItLeavesThePlan()
    {
        var plan = PausePlan<string>.From([E("a", media: MediaState.Playing), E("b", muted: true, media: MediaState.Playing), E("c"), E("d")]);
        plan.Marked("c", 1);
        Assert.True(plan.Holds("a"));
        Assert.True(plan.Holds("c"));
        Assert.False(plan.Holds("b"));
        Assert.False(plan.Holds("d"));
        Assert.False(plan.Holds("x"));
        plan.Forget("a");
        Assert.False(plan.Holds("a"));
    }
}
