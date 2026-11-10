using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests.Sync;

// Voltar à janela do Éteris procura pedidos de computadores novos (0.2.15, T8): nunca antes da 1.ª ronda desta abertura,
// no máximo uma vez a cada 5 minutos, ou logo se a última ronda contou um pedido.
public class ActivationLookTests
{
    private const long Now = 1_762_560_000_000;
    private static readonly long Five = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    [Fact]
    public void BeforeTheFirstRound_NeverPasses()
    {
        var look = new ActivationLook();
        Assert.False(look.TryPass(Now, 0, firstRoundDone: false));
        Assert.False(look.TryPass(Now, 3, firstRoundDone: false));
        // E não gasta a passagem: a primeira depois da 1.ª ronda passa.
        Assert.True(look.TryPass(Now + 1, 0, firstRoundDone: true));
    }

    [Fact]
    public void TwiceInFiveMinutes_WithoutACountedRequest_PassesOnce()
    {
        var look = new ActivationLook();
        Assert.True(look.TryPass(Now, 0, firstRoundDone: true));
        Assert.False(look.TryPass(Now + Five - 1_000, 0, firstRoundDone: true));
        Assert.True(look.TryPass(Now + Five, 0, firstRoundDone: true));
        Assert.False(look.TryPass(Now + Five + 1, 0, firstRoundDone: true));
    }

    [Fact]
    public void WithACountedRequest_PassesAtOnce()
    {
        var look = new ActivationLook();
        Assert.True(look.TryPass(Now, 0, firstRoundDone: true));
        Assert.True(look.TryPass(Now + 1_000, 1, firstRoundDone: true));
        Assert.True(look.TryPass(Now + 2_000, 2, firstRoundDone: true));
    }

    [Fact]
    public void Interval_IsFiveMinutes() => Assert.Equal(TimeSpan.FromMinutes(5), ActivationLook.Interval);
}
