using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;

namespace Eteris.Browser.Tests;

// Os passos de uma thread STA de teste, com o tempo de cada um: se a thread ficar presa, a mensagem da falha diz onde.
public sealed class StaSteps
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<string> _log = new();
    private string _current = "início";

    public TimeSpan Elapsed => _clock.Elapsed;

    public string Current => Volatile.Read(ref _current);

    public string Log => string.Join(" | ", _log);

    public void Mark(string step)
    {
        Volatile.Write(ref _current, step);
        _log.Enqueue($"{_clock.ElapsedMilliseconds} ms {step}");
    }
}

// O único sítio dos testes que abre threads STA (o teste de fonte StaThreads_HaveTimeouts proíbe as outras):
// - thread de fundo e com nome: presa, não segura o processo de testes até ao fim do job;
// - InvokeShutdown no fim, na mesma thread (fecha o dispatcher e a MediaContext que o WPF lhe tenha criado);
// - espera com prazo (60 s, bem acima do que um teste leva num runner carregado) e o passo em que ficou na mensagem;
// - a exceção da thread volta a ser lançada na thread do xunit.
public static class StaRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static void Run(Action work, string name, TimeSpan? timeout = null)
        => Run(_ => { work(); return 0; }, name, timeout: timeout);

    public static T Run<T>(Func<T> work, string name, TimeSpan? timeout = null)
        => Run(_ => work(), name, timeout: timeout);

    // onTimeout corre na thread do xunit quando o prazo acaba com a thread STA ainda viva (por exemplo, repor estado
    // estático global que o finally da thread presa já não vai repor).
    public static T Run<T>(Func<StaSteps, T> work, string name, Action? onTimeout = null, TimeSpan? timeout = null)
    {
        var steps = new StaSteps();
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                steps.Mark("trabalho");
                result = work(steps);
            }
            catch (Exception e) { error = e; }
            finally
            {
                steps.Mark("shutdown");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                steps.Mark("fim");
            }
        })
        {
            IsBackground = true,
            Name = "teste STA: " + name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var limit = timeout ?? DefaultTimeout;
        if (!thread.Join(limit))
        {
            onTimeout?.Invoke();
            throw new Xunit.Sdk.XunitException(
                $"thread STA «{name}» presa no passo «{steps.Current}» ao fim de {limit.TotalSeconds:0.#} s: {steps.Log}");
        }
        if (error is not null) throw new Xunit.Sdk.XunitException($"{error}\npassos: {steps.Log}");
        return result;
    }
}
