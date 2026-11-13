namespace Eteris.Browser.Updates;

// Quando procurar atualizações (INF-07, CAD-06, PRO-08, EGR-02): não só no arranque. Um Éteris que fica semanas aberto, ou
// um portátil só suspenso, nunca ficava a saber de uma correção de segurança; e uma falha de rede no arranque só se
// repetia no arranque seguinte. Regras puras e testadas; UpdateService liga-as à rede e ao relógio.
public static class UpdateSchedule
{
    // De quanto em quanto tempo se pergunta com tudo bem, e a variação (até +30 min) que evita que todos perguntem à mesma hora.
    public static readonly TimeSpan Regular = TimeSpan.FromHours(6);
    public static readonly TimeSpan Jitter = TimeSpan.FromMinutes(30);

    // Depois de 1, 2 e 3 falhas seguidas; à 4.ª volta o ritmo normal (uma rede que não volta não é para martelar).
    public static readonly TimeSpan[] AfterFailure = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    // Quando a rede volta ou o computador retoma: só se a última procura foi há tanto tempo (ou falhou).
    public static readonly TimeSpan WakeAfter = TimeSpan.FromHours(1);

    // Quanto esperar pela procura seguinte; nulo: não há mais (uma versão de desenvolvimento não se atualiza).
    // «jitter»: 0 a 1.
    public static TimeSpan? Next(UpdateState state, int failuresInARow, double jitter)
    {
        if (state == UpdateState.Development) return null;
        if (state == UpdateState.Failed && failuresInARow is >= 1 and <= 3) return AfterFailure[failuresInARow - 1];
        return Regular + Jitter * Math.Clamp(jitter, 0, 1);
    }

    // A rede voltou ou o computador retomou: procura-se já se a última procura falhou ou foi há WakeAfter ou mais.
    public static bool CheckOnWake(UpdateState state, TimeSpan sinceLastCheck)
        => state != UpdateState.Development && (state == UpdateState.Failed || sinceLastCheck >= WakeAfter);
}

// O ciclo «procurar, esperar, procurar…». Sem relógio nem rede próprios (tudo vem de fora), para se poder testar.
public sealed class UpdateScheduler(Func<Task> check, Func<UpdateState> state, Func<TimeSpan, CancellationToken, Task> delay,
                                    Func<double> jitter, Func<DateTime> utcNow)
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _wait;
    private DateTime _lastCheck = DateTime.MinValue;

    public async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            lock (_gate) _lastCheck = utcNow();
            var threw = false;
            try { await check().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { threw = true; }   // a procura nunca derruba o ciclo
            var current = threw ? UpdateState.Failed : state();
            failures = current == UpdateState.Failed ? failures + 1 : 0;
            if (UpdateSchedule.Next(current, failures, jitter()) is not { } wait) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_gate) _wait = cts;
            try { await delay(wait, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Wake (a procura seguinte corre já) ou parar */ }
            finally { lock (_gate) _wait = null; }
        }
    }

    // A rede voltou ou o computador retoma (qualquer fio): corta a espera em curso se a regra o deixar. Durante uma
    // procura não há espera para cortar: a que está a correr já é a pedida.
    public void Wake()
    {
        lock (_gate)
        {
            if (_wait is not { } wait || !UpdateSchedule.CheckOnWake(state(), utcNow() - _lastCheck)) return;
            wait.Cancel();
        }
    }
}
