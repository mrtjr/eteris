using Eteris.Browser.Vault;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Sync;

// O kit de recuperação aberto pela confirmação do Windows: um só de cada vez, num separador (browserId), para um kit
// (a impressão digital), no máximo Limit. Enquanto aberto, copiar, guardar e imprimir não pedem outra vez; «Ver o kit»
// pede sempre (KitGate). Fecha-se do lado do processo: «Esconder», o separador navega, a aba sai da frente ou a janela
// fecha, o prazo, «Sair» e uma cadeia nova (IsOpenFor). Seguro entre threads (um lock).
internal sealed class KitUnlock(Func<long> now)
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private int? _browserId;
    private string? _kitId;
    private long _openedMs;
    private IDisposable? _hold;
    private string? _pendingPath;

    // Abre (fecha o anterior); guarda «hold» (as janelas fora das capturas) para o libertar ao fechar.
    public void Open(int browserId, string kitId, IDisposable? hold = null)
    {
        IDisposable? previous;
        lock (_gate)
        {
            previous = TakeLocked();
            _browserId = browserId;
            _kitId = kitId;
            _openedMs = now();
            _hold = hold;
        }
        previous?.Dispose();
    }

    // Aberto neste separador e dentro do prazo (passado o prazo, fecha).
    public bool IsOpen(int browserId)
    {
        IDisposable? expired = null;
        try
        {
            lock (_gate)
            {
                if (_browserId is null) return false;
                if (now() - _openedMs >= (long)Limit.TotalMilliseconds)
                {
                    expired = TakeLocked();
                    return false;
                }
                return _browserId == browserId;
            }
        }
        finally
        {
            expired?.Dispose();
        }
    }

    // Aberto neste separador e para este kit; um kit diferente (a cadeia mudou) fecha-o.
    public bool IsOpenFor(int browserId, string kitId)
    {
        if (!IsOpen(browserId)) return false;
        IDisposable? closed = null;
        try
        {
            lock (_gate)
            {
                if (_kitId == kitId) return true;
                closed = TakeLocked();
                return false;
            }
        }
        finally
        {
            closed?.Dispose();
        }
    }

    // Idempotente: liberta «hold» e esquece o destino à espera.
    public void Close()
    {
        IDisposable? hold;
        lock (_gate) hold = TakeLocked();
        hold?.Dispose();
    }

    // O separador carregou outro documento (corre na thread do CEF: só um lock e uma comparação).
    public void TabNavigated(int browserId)
    {
        IDisposable? hold = null;
        lock (_gate)
            if (_browserId == browserId) hold = TakeLocked();
        hold?.Dispose();
    }

    // O destino à espera de «Guardar mesmo assim» (só com o kit aberto).
    public void SetPendingPath(string path)
    {
        lock (_gate)
            if (_browserId is not null) _pendingPath = path;
    }

    // Devolve-o e esquece-o.
    public string? TakePendingPath()
    {
        lock (_gate)
        {
            var path = _pendingPath;
            _pendingPath = null;
            return path;
        }
    }

    // Esquece tudo e devolve o «hold» para ser libertado fora do lock (o seu Dispose pode chamar Close).
    private IDisposable? TakeLocked()
    {
        var hold = _hold;
        _browserId = null;
        _kitId = null;
        _openedMs = 0;
        _hold = null;
        _pendingPath = null;
        return hold;
    }
}

internal enum KitShowStep { Refused, AdviceFirst, Reveal }

internal sealed record KitShown(KitShowStep Step, RecoveryKitSecret? Kit = null, string? Advice = null);

// A única porta para o kit (fora daqui ninguém pede as palavras ao agente).
internal static class KitGate
{
    // «Ver o kit». Pede SEMPRE a confirmação do Windows (um kit aberto não a dispensa). Conta sem proteção: o 1.º pedido
    // devolve só o conselho (sem kit, sem abrir nada); o pedido com «acked» mostra-o. «reveal» só depois da confirmação.
    public static async Task<KitShown> ShowAsync(Func<RecoveryKitSecret?> reveal, KitUnlock unlock, int browserId, bool acked,
        Func<ReauthPurpose, Task<bool>> confirm, Func<string?> advice, Func<IDisposable?>? hold = null)
    {
        if (!await SyncChoices.MayRevealKitAsync(confirm).ConfigureAwait(false)) return new(KitShowStep.Refused);
        var text = advice();
        if (text is not null && !acked) return new(KitShowStep.AdviceFirst, Advice: text);
        var kit = reveal();
        if (kit is null) return new(KitShowStep.Refused);
        unlock.Open(browserId, kit.KitId, hold?.Invoke());
        return new(KitShowStep.Reveal, kit, text);
    }

    // Copiar, guardar, imprimir: o kit só se estiver aberto NESTE separador e for o mesmo kit; senão null.
    public static RecoveryKitSecret? Opened(Func<RecoveryKitSecret?> reveal, KitUnlock unlock, int browserId)
    {
        if (!unlock.IsOpen(browserId)) return null;
        var kit = reveal();
        return kit is not null && unlock.IsOpenFor(browserId, kit.KitId) ? kit : null;
    }
}
