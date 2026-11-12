namespace Eteris.Browser.Infrastructure;

// Escreve só a última versão, fora do fio de quem a pede: Post deixa o valor como pendente (substitui o anterior) e uma
// tarefa escreve enquanto houver pendente; WriteNow descarta a pendente, espera a escrita em curso e escreve na thread
// que chama. Nunca há duas escritas ao mesmo tempo, e a pendente lê-se já dentro do trinco da escrita: uma escrita
// antiga nunca passa à frente de uma WriteNow. Failed dispara quando write devolve false (ou lança: nunca derruba o
// processo numa thread do pool).
public sealed class LatestWriter<T>(Func<T, bool> write) where T : class
{
    private readonly Lock _state = new();
    private readonly Lock _writing = new();
    private T? _pending;
    private bool _running;

    public event Action? Failed;

    public bool HasPending
    {
        get { lock (_state) return _pending is not null; }
    }

    public void Post(T value)
    {
        lock (_state)
        {
            _pending = value;
            if (_running) return;
            _running = true;
        }
        Task.Run(Drain);
    }

    public bool WriteNow(T value)
    {
        lock (_state) _pending = null;
        bool ok;
        lock (_writing) ok = Run(value);
        if (!ok) Failed?.Invoke();
        return ok;
    }

    private void Drain()
    {
        while (true)
        {
            bool ok;
            lock (_writing)
            {
                T value;
                lock (_state)
                {
                    if (_pending is null)
                    {
                        _running = false;
                        return;
                    }
                    value = _pending;
                    _pending = null;
                }
                ok = Run(value);
            }
            if (!ok) Failed?.Invoke();
        }
    }

    private bool Run(T value)
    {
        try { return write(value); }
        catch (Exception) { return false; }
    }
}
