namespace Eteris.Browser.Sync;

// Quando é que voltar à janela do Éteris procura pedidos de computadores novos (0.2.15): nunca antes da 1.ª ronda desta
// abertura (no arranque nada sai), no máximo uma vez a cada 5 minutos, ou logo se a última ronda contou um pedido
// (aí só vale o limitador de 5 s de SyncService.Look). Decisão pura, sem relógio próprio.
internal sealed class ActivationLook
{
    // Decisão D8′; a T15 cita esta constante.
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private long? _last;

    // true = pode procurar agora (e regista a hora). firstRoundDone: já acabou uma ronda nesta execução.
    public bool TryPass(long nowMs, int waitingRequests, bool firstRoundDone)
    {
        if (!firstRoundDone) return false;
        lock (_gate)
        {
            if (waitingRequests <= 0 && _last is { } last && nowMs - last < (long)Interval.TotalMilliseconds) return false;
            _last = nowMs;
            return true;
        }
    }
}
