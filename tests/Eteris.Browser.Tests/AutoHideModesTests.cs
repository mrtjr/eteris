using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class AutoHideModesTests
{
    [Theory]
    // foco, mini, ecrã inteiro, pela página → barras escondidas que o rato chama
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, false, false)]
    // vídeo em ecrã inteiro: nunca, nem vindo do modo foco
    [InlineData(false, false, true, true, false)]
    [InlineData(true, false, true, true, false)]
    // ecrã inteiro do menu dentro do modo foco continua
    [InlineData(true, false, true, false, true)]
    // janela flutuante: nunca
    [InlineData(true, true, false, false, false)]
    public void Enabled_FollowsTheModes(bool focus, bool mini, bool fullscreen, bool byPage, bool expected)
        => Assert.Equal(expected, AutoHideBars.Enabled(focus, mini, fullscreen, byPage));

    [Fact]
    public void AltAlone_Reveals()
    {
        var tap = new AltTap();
        tap.KeyDown(isAlt: true, otherModifiers: false);
        Assert.True(tap.KeyUp(isAlt: true));
    }

    [Fact]
    public void AltHeldWithAutoRepeat_StillReveals()
    {
        var tap = new AltTap();
        tap.KeyDown(isAlt: true, otherModifiers: false);
        tap.KeyDown(isAlt: true, otherModifiers: false);
        tap.KeyDown(isAlt: true, otherModifiers: false);
        Assert.True(tap.KeyUp(isAlt: true));
    }

    [Fact]
    public void AltWithAnotherKey_DoesNotReveal()
    {
        var tap = new AltTap();
        tap.KeyDown(isAlt: true, otherModifiers: false);
        tap.KeyDown(isAlt: false, otherModifiers: false); // ← (voltar), Tab, F4
        Assert.False(tap.KeyUp(isAlt: false));
        Assert.False(tap.KeyUp(isAlt: true));
    }

    [Fact]
    public void AltRepeatAfterAnotherKey_DoesNotRearm()
    {
        var tap = new AltTap();
        tap.KeyDown(isAlt: true, otherModifiers: false);
        tap.KeyDown(isAlt: false, otherModifiers: false);
        tap.KeyDown(isAlt: true, otherModifiers: false); // repetição automática do Alt ainda em baixo
        Assert.False(tap.KeyUp(isAlt: true));
    }

    [Fact]
    public void AltClick_AndCtrlAlt_DoNotReveal()
    {
        var tap = new AltTap();
        tap.KeyDown(isAlt: true, otherModifiers: false);
        tap.Cancel();
        Assert.False(tap.KeyUp(isAlt: true));
        tap.KeyDown(isAlt: true, otherModifiers: true);
        Assert.False(tap.KeyUp(isAlt: true));
        // E o gesto seguinte, limpo, volta a funcionar.
        tap.KeyDown(isAlt: true, otherModifiers: false);
        Assert.True(tap.KeyUp(isAlt: true));
    }

    // Menu do navegador aberto a partir da página: o Chromium descarta o keyup das teclas que o Éteris consumiu (o F do
    // Alt+F, o Alt com o menu aberto), por isso o Alt fica «em baixo» e o «Alt sozinho» seguinte perde-se uma vez. O
    // fecho do menu repõe o gesto (MainWindow.BrowserMenu.cs, popup.Closed).
    [Fact]
    public void AltLostKeyUp_ResetRestoresNextTap()
    {
        var lost = new AltTap();
        lost.KeyDown(isAlt: true, otherModifiers: false);
        lost.KeyDown(isAlt: false, otherModifiers: false); // F do Alt+F; o keyup do Alt nunca chega
        lost.KeyDown(isAlt: true, otherModifiers: false);
        Assert.False(lost.KeyUp(isAlt: true));

        var reset = new AltTap();
        reset.KeyDown(isAlt: true, otherModifiers: false);
        reset.KeyDown(isAlt: false, otherModifiers: false);
        reset.Reset();
        reset.KeyDown(isAlt: true, otherModifiers: false);
        Assert.True(reset.KeyUp(isAlt: true));
    }

    [Fact]
    public void Reset_ClearsGesture()
    {
        var t0 = new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);
        var tap = new AltTap();
        tap.KeyDown(isAlt: true, otherModifiers: false, t0);
        tap.Reset();
        Assert.False(tap.TakeGesture(t0.AddMilliseconds(200), TimeSpan.FromSeconds(3)));
        Assert.False(tap.KeyUp(isAlt: true));
    }

    // Espreitar: o Alt do 1.º Alt+clique é largado com o cartão aberto (a camada tem o teclado). Se esse largar não
    // chegar ao AltTap, o Alt fica «em baixo» e o Alt+clique seguinte não dá gesto — era o cartão que não abria depois
    // de fechado com Esc. A camada passa a entregar o KeyUp (MainWindow.Overlay.cs).
    [Fact]
    public void AltReleasedWhileTheCardIsOpen_MustReachTheTap_ForTheNextAltClick()
    {
        var t0 = new DateTime(2026, 11, 8, 6, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromSeconds(3);
        var lost = new AltTap();
        lost.KeyDown(isAlt: true, otherModifiers: false, t0);
        Assert.True(lost.TakeGesture(t0.AddMilliseconds(200), window));
        // Largar perdido (ia para a camada) → o 2.º Alt é tomado por repetição automática e não arma.
        lost.KeyDown(isAlt: true, otherModifiers: false, t0.AddSeconds(2));
        Assert.False(lost.TakeGesture(t0.AddSeconds(2.2), window));

        var delivered = new AltTap();
        delivered.KeyDown(isAlt: true, otherModifiers: false, t0);
        Assert.True(delivered.TakeGesture(t0.AddMilliseconds(200), window));
        delivered.KeyUp(isAlt: true);
        delivered.KeyDown(isAlt: true, otherModifiers: false, t0.AddSeconds(2));
        Assert.True(delivered.TakeGesture(t0.AddSeconds(2.2), window));
    }
}
