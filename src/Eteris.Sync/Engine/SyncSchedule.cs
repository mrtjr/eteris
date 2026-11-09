namespace Eteris.Sync.Engine;

// Quando sincronizar (puro, sem relógio nem temporizador: o anfitrião pergunta Due e marca o temporizador para NextMs).
//
//   - no arranque: só 30 s depois de a janela estar pronta (Startup). O arranque fica para a janela e as páginas, e
//     quem abre o Éteris só para ver uma coisa e o fecha logo não faz pedido nenhum ao Drive;
//   - depois de uma alteração local: 10 s depois da última (espera agregadora: várias alterações seguidas vão juntas),
//     e só se a comparação local (sem rede) encontrar alguma coisa por enviar (LocalCheck);
//   - de 10 em 10 minutos com o Éteris aberto (o que os outros aparelhos mudaram);
//   - já: ao entrar, ao ligar e em «Sincronizar agora» (Now);
//   - depois de uma falha (sem rede, Google em baixo, pasta inacessível): 30 s, 1, 2, 4, 8, 16 e depois de 30 em 30
//     min, sem avisos; as alterações locais não encurtam a espera.
public sealed class SyncSchedule
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    public enum Action { None, LocalCheck, Full }

    private bool _on;
    private long? _periodic;
    private long? _change;
    private long? _retry;
    private int _failures;

    public static TimeSpan Backoff(int failures)
    {
        if (failures <= 0) return TimeSpan.Zero;
        var seconds = 30.0 * Math.Pow(2, Math.Min(failures - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    public int Failures => _failures;

    // Quando acordar (null: nada marcado).
    public long? NextMs
    {
        get
        {
            if (!_on) return null;
            if (_failures > 0) return _retry;
            if (_change is { } change && (_periodic is not { } periodic || change < periodic)) return change;
            return _periodic;
        }
    }

    public void Startup(long nowMs)
    {
        _on = true;
        _periodic = nowMs + (long)StartupDelay.TotalMilliseconds;
    }

    public void Now(long nowMs)
    {
        _on = true;
        _failures = 0;
        _retry = null;
        _periodic = nowMs;
    }

    public void LocalChange(long nowMs)
    {
        if (!_on) return;
        _change = nowMs + (long)ChangeDelay.TotalMilliseconds;
    }

    public Action Due(long nowMs)
    {
        if (!_on) return Action.None;
        if (_failures > 0) return _retry is { } retry && nowMs >= retry ? Action.Full : Action.None;
        if (_periodic is { } periodic && nowMs >= periodic) return Action.Full;
        if (_change is { } change && nowMs >= change) return Action.LocalCheck;
        return Action.None;
    }

    // A comparação local não encontrou nada por enviar.
    public void Checked() => _change = null;

    public void Done(long nowMs, bool ok)
    {
        _change = null;
        if (ok)
        {
            _failures = 0;
            _retry = null;
            _periodic = nowMs + (long)Period.TotalMilliseconds;
        }
        else
        {
            _failures++;
            _retry = nowMs + (long)Backoff(_failures).TotalMilliseconds;
        }
    }

    public void Stop()
    {
        _on = false;
        _periodic = _change = _retry = null;
        _failures = 0;
    }
}
