using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// O temporizador opcional da Pausa (Tabs/PauseTimer.cs): só conta, nunca acaba a Pausa nem toca som.
public class PauseTimerTests
{
    private static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void NoEnd_HasNoCountdown()
    {
        Assert.Null(PauseTimer.Remaining(null));
        Assert.Null(PauseTimer.NextUpdate(null));
        Assert.Equal("Sem fim", PauseTimer.Label(0));
    }

    [Fact]
    public void Remaining_SaysTheWholeMinutesLeft()
    {
        Assert.Equal("faltam 4 min", PauseTimer.Remaining(Min(4)));
        Assert.Equal("faltam 4 min", PauseTimer.Remaining(Min(3.5)));
        Assert.Equal("faltam 15 min", PauseTimer.Remaining(Min(15)));
        Assert.Equal("falta 1 min", PauseTimer.Remaining(Min(1)));
    }

    [Fact]
    public void Remaining_UnderAMinute()
    {
        Assert.Equal("falta menos de 1 min", PauseTimer.Remaining(TimeSpan.FromSeconds(59)));
        Assert.Equal("falta menos de 1 min", PauseTimer.Remaining(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AtTheEnd_ItIsReadyAndNeverEndsThePause()
    {
        Assert.Equal(PauseTimer.Done, PauseTimer.Remaining(TimeSpan.Zero));
        Assert.Equal(PauseTimer.Done, PauseTimer.Remaining(TimeSpan.FromSeconds(-30)));
        Assert.Equal("Pronto. Volte quando quiser.", PauseTimer.Done);
        Assert.True(PauseTimer.Ended(TimeSpan.Zero));
        Assert.False(PauseTimer.Ended(TimeSpan.FromSeconds(1)));
        Assert.False(PauseTimer.Ended(null));
        // Depois do fim, nada mais a atualizar.
        Assert.Null(PauseTimer.NextUpdate(TimeSpan.Zero));
    }

    // Uma atualização por minuto, quando o texto muda (a contagem é uma região viva educada: nada de anúncios a cada segundo).
    [Fact]
    public void NextUpdate_IsWhenTheTextChanges_AtMostAMinute()
    {
        Assert.Equal(Min(1), PauseTimer.NextUpdate(Min(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), PauseTimer.NextUpdate(Min(3.5)));
        Assert.Equal(TimeSpan.FromSeconds(40), PauseTimer.NextUpdate(TimeSpan.FromSeconds(40)));
        // Com 1:00 certo o texto muda já a seguir.
        Assert.Equal(TimeSpan.FromTicks(1), PauseTimer.NextUpdate(Min(1)));
        foreach (var seconds in new[] { 1, 59, 61, 299, 300, 899 })
        {
            var left = TimeSpan.FromSeconds(seconds);
            var next = PauseTimer.NextUpdate(left)!.Value;
            Assert.InRange(next, TimeSpan.FromSeconds(1), Min(1));
            Assert.NotEqual(PauseTimer.Remaining(left), PauseTimer.Remaining(left - next));
            Assert.Equal(PauseTimer.Remaining(left), PauseTimer.Remaining(left - next + TimeSpan.FromMilliseconds(1)));
        }
    }

    [Fact]
    public void Choices_AreNoEndAndOneFiveFifteen()
    {
        Assert.Equal([0, 1, 5, 15], PauseTimer.Choices);
        Assert.Equal(["Sem fim", "1 min", "5 min", "15 min"], PauseTimer.Choices.Select(PauseTimer.Label));
    }

    // Lembrado entre usos, nas definições deste computador (não viaja com a sincronização).
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("15", 15)]
    public void Settings_AcceptOnlyTheChoices(string value, int minutes)
        => Assert.Equal(minutes, new UserSettings().With("pauseMinutes", value)!.PauseMinutes);

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("60")]
    [InlineData("cinco")]
    [InlineData(" 5")]
    public void Settings_RejectOtherValues(string value)
        => Assert.Null(new UserSettings().With("pauseMinutes", value));

    [Fact]
    public void Settings_DefaultToNoEnd_AndStayOnThisComputer()
    {
        Assert.Equal(0, new UserSettings().PauseMinutes);
        Assert.DoesNotContain("pauseMinutes", Eteris.Sync.Model.Collections.SyncableSettings);
    }
}
