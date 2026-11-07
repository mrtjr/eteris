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
}
