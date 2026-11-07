using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class AutoHideBarsTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
    private const double Bars = 90;

    [Fact]
    public void StartsHidden_AndStaysHiddenAwayFromTheTop()
    {
        var bars = new AutoHideBars();
        Assert.False(bars.Revealed);
        Assert.False(bars.Update(cursorY: 300, inside: true, Bars, keepOpen: false, T0));
        Assert.False(bars.Update(cursorY: 1, inside: false, Bars, keepOpen: false, T0));
    }

    [Fact]
    public void TouchingTheTop_Reveals()
    {
        var bars = new AutoHideBars();
        Assert.True(bars.Update(cursorY: 0, inside: true, Bars, keepOpen: false, T0));
        Assert.True(bars.Revealed);
    }

    // Já reveladas, as barras aguentam o rato em qualquer ponto delas (não só na zona quente).
    [Fact]
    public void Revealed_StayWhileTheCursorIsOverTheBars()
    {
        var bars = new AutoHideBars();
        bars.Update(0, true, Bars, false, T0);
        Assert.True(bars.Update(cursorY: 60, inside: true, Bars, keepOpen: false, T0.AddSeconds(5)));
    }

    [Fact]
    public void LeavingTheBars_HidesOnlyAfterTheDelay()
    {
        var bars = new AutoHideBars();
        bars.Update(0, true, Bars, false, T0);
        Assert.True(bars.Update(cursorY: 400, inside: true, Bars, keepOpen: false, T0.AddMilliseconds(100)));
        Assert.True(bars.Update(cursorY: 400, inside: true, Bars, keepOpen: false, T0.AddMilliseconds(100) + AutoHideBars.HideDelay / 2));
        Assert.False(bars.Update(cursorY: 400, inside: true, Bars, keepOpen: false, T0.AddMilliseconds(100) + AutoHideBars.HideDelay));
    }

    // Voltar às barras antes do prazo cancela a contagem.
    [Fact]
    public void ComingBack_ResetsTheDelay()
    {
        var bars = new AutoHideBars();
        bars.Update(0, true, Bars, false, T0);
        bars.Update(400, true, Bars, false, T0.AddMilliseconds(100));
        bars.Update(40, true, Bars, false, T0.AddMilliseconds(600));
        Assert.True(bars.Update(400, true, Bars, false, T0.AddMilliseconds(1200)));
        Assert.False(bars.Update(400, true, Bars, false, T0.AddMilliseconds(1200) + AutoHideBars.HideDelay));
    }

    // Com a barra de endereço focada ou um menu aberto, as barras não desaparecem debaixo do utilizador.
    [Fact]
    public void KeepOpen_HoldsTheBarsRegardlessOfTheCursor()
    {
        var bars = new AutoHideBars();
        Assert.True(bars.Update(cursorY: 500, inside: false, Bars, keepOpen: true, T0));
        Assert.True(bars.Update(cursorY: 500, inside: false, Bars, keepOpen: true, T0.AddMinutes(1)));
        // Deixou de haver motivo: a contagem para esconder começa agora, não antes.
        Assert.True(bars.Update(cursorY: 500, inside: false, Bars, keepOpen: false, T0.AddMinutes(1)));
        Assert.False(bars.Update(cursorY: 500, inside: false, Bars, keepOpen: false, T0.AddMinutes(1) + AutoHideBars.HideDelay));
    }

    [Fact]
    public void RevealFor_ShowsTheBarsForAWhile_ThenTheyGoWithTheCursor()
    {
        var bars = new AutoHideBars();
        bars.RevealFor(AutoHideBars.KeyboardHold, T0);
        Assert.True(bars.Revealed);
        Assert.True(bars.Update(500, true, Bars, false, T0.AddSeconds(2)));
        var after = T0 + AutoHideBars.KeyboardHold;
        Assert.True(bars.Update(500, true, Bars, false, after));
        Assert.False(bars.Update(500, true, Bars, false, after + AutoHideBars.HideDelay));
    }

    [Fact]
    public void Hide_ResetsEverything()
    {
        var bars = new AutoHideBars();
        bars.RevealFor(AutoHideBars.KeyboardHold, T0);
        bars.Hide();
        Assert.False(bars.Revealed);
        Assert.False(bars.Update(500, true, Bars, false, T0.AddSeconds(1)));
    }
}
