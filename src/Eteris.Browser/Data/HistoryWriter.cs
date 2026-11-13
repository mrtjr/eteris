using System.Threading.Channels;

namespace Eteris.Browser.Data;

// Escritas do histórico fora da thread da janela (docs/motor.md, F5): cada visita e cada mudança de título eram uma
// escrita síncrona no SQLite (com fsync), 3–6 ms da thread da janela por navegação, e uma por cada mudança de título
// em sites com contadores no título. Agora vão para uma fila com uma só tarefa a escrevê-las, pela ordem em que
// chegaram. Os títulos juntam-se por endereço: só o último de cada intervalo (titleDelay) é escrito.
// Quem lê ou apaga primeiro despeja a fila (Flush): nunca vê o histórico atrasado nem apaga antes de uma escrita
// pendente.
public sealed class HistoryWriter : IDisposable
{
    private readonly Func<HistoryStore> _store;
    private readonly TimeSpan _titleDelay;
    private readonly Channel<Action<HistoryStore>> _queue = Channel.CreateUnbounded<Action<HistoryStore>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private int _pending;

    // Escritas feitas (para os testes).
    internal int Written => Volatile.Read(ref _written);
    private int _written;
    private TaskCompletionSource _idle = NewIdle(completed: true);

    public HistoryWriter(Func<HistoryStore> store, TimeSpan titleDelay)
    {
        _store = store;
        _titleDelay = titleDelay;
        _worker = Task.Run(RunAsync);
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) idle.SetResult();
        return idle;
    }

    // A escrita entra na fila e a thread que chama segue logo.
    public void Enqueue(Action<HistoryStore> write)
    {
        lock (_gate)
        {
            if (_pending++ == 0) _idle = NewIdle(completed: false);
        }
        if (!_queue.Writer.TryWrite(write)) Done();
    }

    // O título mais recente de url é escrito daqui a titleDelay (os intermédios perdem-se de propósito).
    public void UpdateTitleLater(string url, string title)
    {
        bool first;
        lock (_gate)
        {
            first = !_titles.ContainsKey(url);
            _titles[url] = title;
        }
        if (first) _ = Task.Delay(_titleDelay).ContinueWith(_ => WriteTitle(url), TaskScheduler.Default);
    }

    private void WriteTitle(string url)
    {
        string? title;
        lock (_gate)
        {
            if (!_titles.Remove(url, out title)) return;
        }
        Enqueue(h => h.UpdateTitle(url, title));
    }

    // Trabalho longo e raro (a compactação, VACUUM) ao lado da fila, uma de cada vez: não conta para o Flush, por isso
    // quem lê ou apaga (omnibox, paleta, barra lateral, eteris://history) não espera pela compactação em si. Com o WAL,
    // as leituras seguem durante o VACUUM, mas uma escrita espera pela vez dela no SQLite: uma visita que entre na fila
    // durante a compactação fica à espera no escritor, e quem despejar a fila nesse intervalo ainda espera por ela (até
    // aos 2 s do FlushHistory). Uma falha fica aqui, como nas escritas da fila.
    private readonly Lock _aside = new();

    public Task RunAside(Action<HistoryStore> work) => Task.Run(() => RunAsideNow(work));

    // O mesmo, já, na thread que chama, à espera da que esteja a correr.
    public void RunAsideNow(Action<HistoryStore> work)
    {
        lock (_aside)
        {
            try { work(_store()); }
            catch (Exception) { }
        }
    }

    // O mesmo, mas espera no máximo timeout pela que esteja a correr; passado ele, desiste sem correr e devolve false
    // (a compactação única ao sair não segura o fecho do Éteris atrás de um VACUUM grande de «Apagar tudo»).
    public bool RunAsideNow(Action<HistoryStore> work, TimeSpan timeout)
    {
        if (!_aside.TryEnter(timeout)) return false;
        try
        {
            try { work(_store()); }
            catch (Exception) { }
            return true;
        }
        finally { _aside.Exit(); }
    }

    // Espera (no máximo timeout) que tudo o que já foi pedido esteja escrito, incluindo os títulos à espera.
    public bool Flush(TimeSpan timeout)
    {
        string[] urls;
        lock (_gate) urls = [.. _titles.Keys];
        foreach (var url in urls) WriteTitle(url);
        Task idle;
        lock (_gate) idle = _idle.Task;
        return idle.Wait(timeout);
    }

    private async Task RunAsync()
    {
        await foreach (var write in _queue.Reader.ReadAllAsync())
        {
            // O histórico é conveniência: uma falha da base de dados nunca pode interromper a navegação.
            try { write(_store()); }
            catch (Exception) { }
            Interlocked.Increment(ref _written);
            Done();
        }
    }

    private void Done()
    {
        lock (_gate)
        {
            if (--_pending == 0) _idle.TrySetResult();
        }
    }

    // Ao sair: escreve o que falta (até 3 s) e fecha a fila.
    public void Dispose()
    {
        Flush(TimeSpan.FromSeconds(3));
        _queue.Writer.TryComplete();
        _worker.Wait(TimeSpan.FromSeconds(1));
    }
}
