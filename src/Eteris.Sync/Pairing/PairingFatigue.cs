namespace Eteris.Sync.Pairing;

// Pedidos em série (0.2.15): quem tem a conta Google pode escrever pedidos falsos no Drive sem parar, à espera de que
// a pessoa acabe por escrever um código que alguém lhe dite. Quando MaxBad pedidos acabam mal dentro da janela (recusa,
// 3 códigos errados, expirados ou cancelados do outro lado depois de este computador responder), a barra deixa de
// abrir sozinha. Só se guardam horas, neste computador; nunca vão para o transporte. Decisão pura.
public static class PairingFatigue
{
    public const int MaxBad = 3;                                        // PROPOSTA (o dono confirma)
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);    // PROPOSTA (o dono confirma)

    // Junta «um pedido acabou mal agora»; deita fora as horas que já saíram da janela; guarda no máximo MaxBad. Uma
    // hora guardada no futuro (relógio mexido) passa a «agora»; mais de uma janela no futuro sai.
    public static long[] Add(IReadOnlyList<long>? bad, long nowMs)
    {
        var kept = (bad ?? []).Where(t => Counts(t, nowMs)).Select(t => Math.Min(t, nowMs))
            .Append(nowMs).Order().ToList();
        return kept.Skip(Math.Max(0, kept.Count - MaxBad)).ToArray();
    }

    // true = a barra não abre sozinha: há MaxBad horas dentro da janela. Uma hora no futuro (relógio mexido) conta
    // como «agora», para não calar a barra para sempre (uma hora só conta até sair da janela); mais de uma janela no
    // futuro não conta (com o relógio recuado, a barra ficava calada até o relógio passar essas horas).
    public static bool Quiet(IReadOnlyList<long>? bad, long nowMs)
        => bad is not null && bad.Count(t => Counts(t, nowMs)) >= MaxBad;

    private static bool Counts(long t, long nowMs)
    {
        var window = (long)Window.TotalMilliseconds;
        return t - nowMs <= window && nowMs - Math.Min(t, nowMs) < window;
    }
}
