using Eteris.Sync.Pairing;

namespace Eteris.Browser.Tests.Sync;

// A renovação automática do pedido do computador novo (0.2.15, T7): só enquanto nenhum anfitrião respondeu, só com o
// ecrã do pedido à vista, até meia hora desde o primeiro pedido e, no máximo, duas vezes (três sessões por gesto).
public class PairingRenewalTests
{
    private const long First = 1_762_560_000_000;
    private static readonly long Minute = 60_000;

    [Theory]
    [InlineData(false, true, 10, 0, true)]
    [InlineData(false, true, 29, 1, true)]
    [InlineData(true, true, 10, 0, false)]    // um anfitrião respondeu: nunca renova
    [InlineData(false, false, 10, 0, false)]  // o ecrã não está à vista
    [InlineData(false, true, 30, 0, false)]   // meia hora desde o primeiro pedido
    [InlineData(false, true, 45, 0, false)]
    [InlineData(false, true, 20, 2, false)]   // já renovou duas vezes
    public void MayRenew_OnlyWhileNobodyAnswered_Visible_UnderThirtyMinutes_AndTwice(bool hostAnswered, bool visible, int minutes, int renewals, bool expected)
        => Assert.Equal(expected, PairingRenewal.MayRenew(hostAnswered, visible, First, First + minutes * Minute, renewals));

    [Fact]
    public void Limits()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), PairingRenewal.Window);
        Assert.Equal(2, PairingRenewal.MaxRenewals);
    }
}
