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
}
