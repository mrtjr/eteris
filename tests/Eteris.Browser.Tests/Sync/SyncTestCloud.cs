using Eteris.Sync.Engine;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// O «Drive» dos testes de sincronização com vários agentes: um transporte em memória (sem disco: uma pasta real em
// %TEMP% dá erros passageiros com a bateria em paralelo) que avisa cada escrita e cada apagamento, e esperas que
// acordam com esses avisos e com o Changed dos agentes — nunca com um orçamento de tempo. O limite de 2 minutos só
// existe contra um bloqueio e diz o estado de cada agente.
internal sealed class SyncTestCloud : ISyncTransport
{
    private readonly SemaphoreSlim _pulse = new(0);
    private readonly List<(string Name, SyncAgent Agent)> _agents = [];

    public MemoryTransport Memory { get; } = new();

    // Só contra um bloqueio; nunca é o caminho normal de um teste.
    public TimeSpan Guard { get; init; } = TimeSpan.FromMinutes(2);

    public string Name => Memory.Name;

    // Nunca lança: só liberta o semáforo (que nunca se descarta: uma escrita de fundo ainda em curso, como o x-host de
    // um cancelamento, pode avisar depois do fim do teste).
    public void Pulse() => _pulse.Release();

    // Um agente deste teste: o Changed dele acorda as esperas e, no fim, o agente para (StopAll).
    public SyncAgent Watch(string name, SyncAgent agent)
    {
        lock (_agents) _agents.Add((name, agent));
        agent.Changed += Pulse;
        return agent;
    }

    // Outro transporte (o Drive falso, um que falha…) a avisar as mesmas esperas.
    public ISyncTransport Signalling(ISyncTransport inner) => new SignalTransport(inner, Pulse);

    // No fim do teste: nenhum ciclo de sondagem fica vivo. Só escreve no estado guardado de cada agente.
    public void StopAll()
    {
        List<(string Name, SyncAgent Agent)> agents;
        lock (_agents) agents = [.. _agents];
        foreach (var (_, agent) in agents)
        {
            agent.Changed -= Pulse;
            agent.SignOut(forgetKey: false);
        }
    }

    // Espera até a condição se cumprir. A condição avalia-se sempre AQUI (nunca dentro do Changed: lá dentro um agente
    // pode ter o seu trinco). Um aviso entre a avaliação e o WaitAsync fica contado no semáforo (não se perde).
    public async Task WaitFor(Func<bool> condition, string what)
    {
        var guard = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            var left = Guard - guard.Elapsed;
            if (left <= TimeSpan.Zero) Assert.Fail("À espera de: " + what + " | " + Describe());
            await _pulse.WaitAsync(left);
        }
    }

    // O mesmo, com uma condição que faz pedidos (ler o transporte, procurar pedidos).
    public async Task WaitFor(Func<Task<bool>> condition, string what)
    {
        var guard = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            var left = Guard - guard.Elapsed;
            if (left <= TimeSpan.Zero) Assert.Fail("À espera de: " + what + " | " + Describe());
            await _pulse.WaitAsync(left);
        }
    }

    public bool Has(string path) => Memory.Files.ContainsKey(path);

    public IReadOnlyList<string> Paths(string prefix = SyncPaths.Root)
        => Memory.Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

    public int PairingFiles(string step)
        => Memory.Files.Keys.Count(f => f.StartsWith(SyncPaths.Root + "pair/", StringComparison.Ordinal) && f.EndsWith("/" + step, StringComparison.Ordinal));

    // Diagnóstico de uma espera que não acabou: o estado de cada agente (nunca o valor do código) e os ficheiros do
    // emparelhamento.
    public string Describe()
    {
        List<(string Name, SyncAgent Agent)> agents;
        lock (_agents) agents = [.. _agents];
        var parts = agents.Select(a =>
        {
            var s = a.Agent.Status;
            return $"{a.Name}: Phase={s.Phase} Running={s.Running} Error={s.Error ?? "-"} Code={(s.Pairing?.Code is null ? "não" : "sim")} "
                + $"PairError={s.Pairing?.Error ?? "-"} End={s.Pairing?.End.ToString() ?? "-"} Requests={s.Requests.Count} "
                + $"Answering={s.AnsweringSinceMs != 0} Waiting={s.WaitingRequests} Quiet={s.PairingQuiet}";
        });
        return string.Join(" ; ", parts) + " | pair/: " + string.Join(", ", Paths(SyncPaths.Root + "pair/"));
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => Memory.ListAsync(prefix, cancellationToken);

    public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => Memory.ReadAsync(path, cancellationToken);

    public async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        await Memory.WriteAsync(path, data, cancellationToken);
        Pulse();
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        await Memory.DeleteAsync(path, cancellationToken);
        Pulse();
    }

    private sealed class SignalTransport(ISyncTransport inner, Action changed) : ISyncTransport
    {
        public string Name => inner.Name;

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);

        public async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(path, data, cancellationToken);
            changed();
        }

        public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            await inner.DeleteAsync(path, cancellationToken);
            changed();
        }
    }
}
