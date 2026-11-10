namespace Eteris.Sync.Pairing;

// O pedido do aparelho novo renova-se sozinho (sessão nova no mesmo pedido) só enquanto nenhum aparelho ligado
// respondeu, só com o ecrã do pedido à vista e durante no máximo meia hora desde o primeiro pedido. Limite duro, para
// não depender do relógio: duas renovações (um pedido + duas renovações = três sessões de 10 minutos por gesto).
public static class PairingRenewal
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    public const int MaxRenewals = 2;

    public static bool MayRenew(bool hostAnswered, bool screenVisible, long firstRequestMs, long nowMs, int renewals) =>
        !hostAnswered && screenVisible && renewals < MaxRenewals
        && nowMs - firstRequestMs < (long)Window.TotalMilliseconds;
}
