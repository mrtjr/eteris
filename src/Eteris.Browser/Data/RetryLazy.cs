using System.Runtime.ExceptionServices;

namespace Eteris.Browser.Data;

// Um Lazy que não guarda a falha para sempre: uma exceção ou um null contam como falha e a criação repete-se passado
// retryAfter; o que se criou fica. Duas threads que cheguem juntas esperam pela mesma criação. Reset esquece o valor e a
// falha sem esperar por ninguém (pode vir de dentro de make, ou enquanto outra thread cria): o valor de uma criação que
// começou antes do Reset devolve-se a quem o pediu mas não se guarda.
public sealed class RetryLazy<T>(Func<T?> make, TimeSpan retryAfter, Func<DateTime>? utcNow = null) where T : class
{
    private sealed class State(T? value, Exception? failure, DateTime at, bool tried)
    {
        public readonly T? Value = value;
        public readonly Exception? Failure = failure;
        public readonly DateTime At = at;
        public readonly bool Tried = tried;
    }

    private readonly Lock _creating = new();
    private readonly Func<DateTime> _now = utcNow ?? (() => DateTime.UtcNow);
    private State _state = new(null, null, default, tried: false);

    public bool IsValueCreated => Volatile.Read(ref _state).Value is not null;

    // O valor, ou null se (ainda) não se conseguiu criar.
    public T? Value => Resolve(throwOnFailure: false);

    // O valor, ou a última falha (InvalidOperationException se make devolveu null).
    public T Required => Resolve(throwOnFailure: true)!;

    public void Reset() => Volatile.Write(ref _state, new State(null, null, default, tried: false));

    private bool ShouldTry(State state) => !state.Tried || _now() - state.At >= retryAfter;

    private T? Resolve(bool throwOnFailure)
    {
        var state = Volatile.Read(ref _state);
        if (state.Value is not null) return state.Value;
        if (!ShouldTry(state)) return Failed(state, throwOnFailure);
        lock (_creating)
        {
            state = Volatile.Read(ref _state);
            if (state.Value is not null) return state.Value;
            if (!ShouldTry(state)) return Failed(state, throwOnFailure);
            T? made = null;
            Exception? failure = null;
            try { made = make(); }
            catch (Exception e) { failure = e; }
            var next = made is not null ? new State(made, null, default, tried: true)
                : new State(null, failure ?? new InvalidOperationException("A criação devolveu nada."), _now(), tried: true);
            // Um Reset entretanto trocou o estado: esta criação é de antes dele e não se guarda.
            Interlocked.CompareExchange(ref _state, next, state);
            return made is not null ? made : Failed(next, throwOnFailure);
        }
    }

    private static T? Failed(State state, bool throwOnFailure)
    {
        if (throwOnFailure && state.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
        return null;
    }
}
