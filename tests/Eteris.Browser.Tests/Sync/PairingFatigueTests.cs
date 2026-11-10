using Eteris.Sync.Pairing;

namespace Eteris.Browser.Tests.Sync;

// Pedidos em série (0.2.15, T8): quando 3 pedidos acabam mal em 24 h, a barra de aprovação deixa de abrir sozinha.
public class PairingFatigueTests
{
    private const long Now = 1_762_560_000_000;
    private static readonly long Hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    [Fact]
    public void ThreeBadInADay_GoesQuiet_AndForgets()
    {
        Assert.Equal(3, PairingFatigue.MaxBad);
        Assert.Equal(TimeSpan.FromHours(24), PairingFatigue.Window);
        Assert.False(PairingFatigue.Quiet(null, Now));
        Assert.False(PairingFatigue.Quiet([], Now));

        var bad = PairingFatigue.Add(null, Now - 20 * Hour);
        bad = PairingFatigue.Add(bad, Now - Hour);
        Assert.False(PairingFatigue.Quiet(bad, Now));
        bad = PairingFatigue.Add(bad, Now);
        Assert.True(PairingFatigue.Quiet(bad, Now));
        // A mais antiga sai da janela de 24 h: volta a abrir.
        Assert.False(PairingFatigue.Quiet(bad, Now + 4 * Hour + 1));

        // Add deita fora as antigas e nunca guarda mais de MaxBad.
        var many = PairingFatigue.Add([Now - 30 * Hour, Now - 3, Now - 2, Now - 1], Now);
        Assert.Equal([Now - 2, Now - 1, Now], many);
        Assert.Equal([Now], PairingFatigue.Add([Now - 25 * Hour], Now));

        // Uma hora no futuro (relógio mexido) conta como «agora»: não cala a barra para sempre.
        Assert.True(PairingFatigue.Quiet([Now + 1000 * Hour, Now, Now], Now));
        Assert.False(PairingFatigue.Quiet([Now + 1000 * Hour, Now, Now], Now + 25 * Hour));
        Assert.Equal([Now, Now], PairingFatigue.Add([Now + 1000 * Hour], Now));
    }
}
