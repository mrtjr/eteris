using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// Browser/PageSwap.cs, 1.º documento de uma aba (aba nova): a página antiga só sai quando a 1.ª pintura com conteúdo já
// foi apresentada (medido a 60 fps: com o rAF duplo sozinho, 5–6 fotogramas brancos em 2 de 9 sessões na 1.ª aba nova;
// com a espera, 0 em 24 aberturas e a mesma latência).
public class PageSwapFirstPaintTests
{
    [Fact]
    public void FirstPaintScript_WaitsForTheFirstContentfulPaintAndFallsBackToTheDoubleRaf()
    {
        Assert.Contains("first-contentful-paint", PageSwap.FirstPaintScript);
        Assert.Contains("PerformanceObserver", PageSwap.FirstPaintScript);
        Assert.Contains("buffered: true", PageSwap.FirstPaintScript);
        Assert.Contains($"setTimeout(() => done(painted()), {(int)PageSwap.FirstPaintWait.TotalMilliseconds})", PageSwap.FirstPaintScript);
        Assert.Contains($"return '{PageSwap.OnScreen}'", PageSwap.FirstPaintScript);
        Assert.EndsWith(PageSwap.NextFrameScript, PageSwap.FirstPaintScript.TrimEnd());
    }

    [Fact]
    public void FirstPaintWait_IsBoundedByTheSwapLimit()
        => Assert.True(PageSwap.FirstPaintWait > TimeSpan.Zero && PageSwap.FirstPaintWait < PageSwap.HoldLimit);

    [Theory]
    [InlineData(true, "no-ecra", PageSwap.Presented.OnScreen)]
    [InlineData(true, true, PageSwap.Presented.Frame)]
    [InlineData(true, false, PageSwap.Presented.No)]
    [InlineData(true, null, PageSwap.Presented.No)]
    [InlineData(true, "outra", PageSwap.Presented.No)]
    [InlineData(false, "no-ecra", PageSwap.Presented.No)]
    [InlineData(false, true, PageSwap.Presented.No)]
    public void FromScript(bool success, object? result, PageSwap.Presented expected)
        => Assert.Equal(expected, PageSwap.FromScript(success, result));

    [Fact]
    public void Margin_OnlyWhenSomeArrivalIsNotProvenOnScreen()
    {
        Assert.False(PageSwap.NeedsMargin([PageSwap.Presented.OnScreen]));
        Assert.False(PageSwap.NeedsMargin([PageSwap.Presented.OnScreen, PageSwap.Presented.OnScreen]));
        Assert.True(PageSwap.NeedsMargin([PageSwap.Presented.OnScreen, PageSwap.Presented.Frame]));
        Assert.True(PageSwap.NeedsMargin([PageSwap.Presented.No]));
        // Sem nenhuma página a entrar não há nada por apresentar.
        Assert.False(PageSwap.NeedsMargin([]));
    }
}
