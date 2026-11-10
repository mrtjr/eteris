using System.IO;
using Eteris.Browser.Sync;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// O estado do agente lido de fora da ronda (revisão final da 0.2.15, A-01): a interface lê Status e grava escolhas
// noutras threads enquanto uma ronda corre. O estado guardado (SavedSync.Engine) é sempre uma cópia que mais ninguém
// muda — nunca o estado vivo do motor —, o nome novo só chega ao motor na ronda, e um assinante de Changed que lança
// não estraga a operação que o avisou.
public sealed class SyncAgentThreadingTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(1, ct);
    private long _now = 1_762_560_000_000;
    private readonly SyncTestCloud _cloud = new();

    private long Now() => _now;

    public void Dispose() => _cloud.StopAll();

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, ISyncStore Store);

    private Device NewDevice(string name, ISyncStore? store = null, MemoryProfile? profile = null, Func<ISyncTransport>? transport = null)
    {
        profile ??= new MemoryProfile();
        store ??= new MemoryStore();
        var agent = new SyncAgent(transport ?? (() => _cloud), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(1) };
        _cloud.Watch(name, agent);
        return new Device(agent, profile, store);
    }

    private async Task<Device> JoinAsync(Device host, string name)
    {
        var device = NewDevice(name);
        await device.Agent.StartAsync();
        await device.Agent.JoinWithCodeAsync(host.Agent.RevealKit()!.Words);
        Assert.Equal(SyncPhase.On, device.Agent.Status.Phase);
        return device;
    }

    // Um motor de teste com o segredo da conta: escreve o estado de um aparelho novo (com a sua chave pública).
    private async Task AddPeerAsync(Device host, string name)
    {
        using var secret = AccountSecret.FromCode(host.Agent.RevealKit()!.Words);
        var engine = await SyncEngine.JoinAsync(_cloud, secret, DeviceIdentity.Create(name), Now);
        await engine.PullAsync();
    }

    // Cada estado do motor entregue a uma gravação fica como estava: se mudar depois, era o estado vivo do motor.
    private sealed class WatchingStore : ISyncStore
    {
        private readonly MemoryStore _inner = new();
        private readonly List<(SyncState State, string Json)> _seen = [];

        public SavedSync? Load() => _inner.Load();

        public void Save(SavedSync saved)
        {
            if (saved.Engine is { } state && !_seen.Any(s => ReferenceEquals(s.State, state)))
                _seen.Add((state, Text(state)));
            _inner.Save(saved);
        }

        public void Delete() => _inner.Delete();

        public int Seen => _seen.Count;

        public int Changed => _seen.Count(s => Text(s.State) != s.Json);

        private static string Text(SyncState state) => System.Text.Encoding.UTF8.GetString(state.ToBytes());
    }

    [Fact]
    public async Task SavedEngine_IsNeverTheLiveStateOfTheEngine()
    {
        var store = new WatchingStore();
        var casa = NewDevice("Casa", store);
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        await casa.Agent.StartAsync();
        var trabalho = await JoinAsync(casa, "Trabalho");
        for (var round = 0; round < 3; round++)
        {
            _now += 1000;
            casa.Profile.Bookmark("https://exemplo.example/" + round, "Página " + round);
            Assert.True(await casa.Agent.RunAsync());
            Assert.True(await trabalho.Agent.RunAsync());
        }
        await casa.Agent.RefreshDevicesAsync();
        // Reabrir: o motor retomado também não fica com o objeto guardado.
        var reaberta = NewDevice("Casa", store, casa.Profile);
        _now += 1000;
        reaberta.Profile.Bookmark("https://exemplo.example/reaberta", "Depois de reabrir");
        Assert.True(await reaberta.Agent.RunAsync());
        await reaberta.Agent.NewRecoveryCodeAsync();
        Assert.True(await reaberta.Agent.RunAsync());

        Assert.Equal(0, store.Changed);
        Assert.True(store.Seen > 3, "só " + store.Seen + " estados guardados");
        Assert.Equal("Depois de reabrir", (await SettledTitle(trabalho, "https://exemplo.example/reaberta")));
    }

    private static async Task<string?> SettledTitle(Device device, string url)
    {
        for (var round = 0; round < 3; round++) Assert.True(await device.Agent.RunAsync());
        return device.Profile.Title(url);
    }

    // Transporte que segura a leitura do estado do 2.º aparelho alheio (a meio de SyncEngine.DevicesAsync, já com a
    // chave do 1.º fixada no estado vivo do motor).
    private sealed class HoldSecondPeer(ISyncTransport inner, string chain, string self) : ISyncTransport
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _peers;

        public Task Arrived => _arrived.Task;

        public void Release() => _release.TrySetResult();

        public string Name => inner.Name;

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);

        public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (SyncPaths.TryParseState(chain, path, out var device) && device != self && Interlocked.Increment(ref _peers) == 2)
            {
                _arrived.TrySetResult();
                await _release.Task;
            }
            return await inner.ReadAsync(path, cancellationToken);
        }

        public Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(path, data, cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);
    }

    [Fact]
    public async Task Status_DuringARound_NeverReadsTheLiveEngine()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        Assert.Equal(1, casa.Agent.Status.Kit!.KnownDevices);
        await JoinAsync(casa, "Trabalho");
        await JoinAsync(casa, "Escritório");

        // A Casa reabre (ainda sem lista de aparelhos nem chaves fixadas) e a ronda fica a meio da leitura dos aparelhos.
        var saved = casa.Store.Load()!;
        var hold = new HoldSecondPeer(_cloud, saved.Engine!.ChainId, saved.Engine.Device.Id);
        var reaberta = NewDevice("Casa", casa.Store, casa.Profile, () => hold);
        var round = reaberta.Agent.RunAsync();
        await hold.Arrived.WaitAsync(TimeSpan.FromMinutes(2));
        try
        {
            // A meio: a contagem é a do estado guardado, e ler, adiar o kit, mudar coleções e mudar o nome não tocam no motor.
            Assert.True(reaberta.Agent.Status.Running);
            Assert.Equal(1, reaberta.Agent.Status.Kit!.KnownDevices);
            reaberta.Agent.SnoozeKit(_now + 1);
            reaberta.Agent.SetCollections([], on: true);
            await reaberta.Agent.RenameDeviceAsync("Casa nova");
            Assert.Equal(1, reaberta.Agent.Status.Kit!.KnownDevices);
            Assert.Equal("Casa nova", reaberta.Agent.DeviceName);
        }
        finally { hold.Release(); }
        Assert.True(await round);
        Assert.Equal(3, reaberta.Agent.Status.Kit!.KnownDevices);
        Assert.Equal(3, reaberta.Agent.Status.Devices.Count);
        // O nome novo chega ao motor (e aos outros) na ronda seguinte.
        Assert.True(await reaberta.Agent.RunAsync());
        Assert.Equal("Casa nova", reaberta.Store.Load()!.Engine!.Device.Name);
    }

    [Fact]
    public async Task ReadsAndSaves_FromAnotherThread_WhileRoundsRun_NeverThrow()
    {
        var casa = NewDevice("Casa");
        await casa.Agent.StartAsync();
        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        long reads = 0;
        // A «interface»: lê o estado e grava escolhas sem parar, como RefreshBars, o lembrete do kit e as Definições.
        var reader = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var status = casa.Agent.Status;
                    GC.KeepAlive(status.Kit?.KnownDevices);
                    casa.Agent.SnoozeKit(Interlocked.Increment(ref reads));
                    casa.Agent.SetCollections([], on: true);
                    GC.KeepAlive(casa.Agent.HasSecrets);
                }
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true, Name = "leitor do estado" };
        reader.Start();
        try
        {
            // As rondas: em cada uma há registos novos (caixa de saída, lotes) e, nas primeiras 60, um aparelho novo por
            // fixar. Continuam até o leitor ter lido bastante ao mesmo tempo (numa máquina lenta ou carregada, mais rondas).
            for (var round = 0; (round < 60 || Interlocked.Read(ref reads) < 300) && round < 600 && failure is null; round++)
            {
                _now += 1000;
                for (var i = 0; i < 10; i++) casa.Profile.Bookmark($"https://exemplo.example/{round}/{i}", "Página");
                if (round < 60) await AddPeerAsync(casa, "Par " + round);
                Assert.True(await casa.Agent.RunAsync());
                await casa.Agent.RefreshDevicesAsync();
            }
        }
        finally
        {
            stop.Cancel();
            Assert.True(reader.Join(TimeSpan.FromMinutes(2)), "o leitor não parou");
        }
        Assert.Null(failure);
        Assert.True(Interlocked.Read(ref reads) >= 300, "só " + reads + " leituras");
        Assert.Null(casa.Agent.Status.Error);
    }

    [Fact]
    public async Task ChangedSubscriber_ThatThrows_BreaksNothing()
    {
        var casa = NewDevice("Casa");
        var calls = 0;
        casa.Agent.Changed += () => throw new InvalidOperationException("Collection was modified");
        casa.Agent.Changed += () => Interlocked.Increment(ref calls);

        // Nenhuma operação deixa sair a exceção de um assinante, e os assinantes seguintes continuam a ser avisados.
        await casa.Agent.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Agent.Status.Phase);
        Assert.False(casa.Agent.Status.Running);
        Assert.True(Volatile.Read(ref calls) > 0);
        casa.Agent.SetCollections([], on: true);
        casa.Agent.SnoozeKit(_now + 1);
        await casa.Agent.RenameDeviceAsync("Casa nova");

        // A ronda seguinte ainda corre (o trinco das rondas foi libertado).
        var next = casa.Agent.RunAsync();
        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromMinutes(2))));
        Assert.True(await next);
        await casa.Agent.RefreshDevicesAsync().WaitAsync(TimeSpan.FromMinutes(2));
        await casa.Agent.NewRecoveryCodeAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.False(casa.Agent.Status.Running);

        // «Sair» chega ao fim: quem chamou segue (no navegador, a seguir saem os tokens da Google).
        casa.Agent.SignOut(forgetKey: true);
        Assert.Equal(SyncPhase.Off, casa.Agent.Status.Phase);
        Assert.Null(casa.Store.Load());
    }

    // O nome novo e a contagem dos aparelhos não tocam no motor fora da ronda (o motor é só de quem tem a ronda).
    [Fact]
    public void Rename_AndKnownDevices_DoNotTouchTheEngine()
    {
        var src = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Sync", "Engine", "SyncAgent.cs"));
        Assert.DoesNotContain("_engine?.Rename", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_engine?.State ?? saved.Engine) is not { } state) return 1", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Engine = engine.State;", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Engine = rotated.State;", src, StringComparison.Ordinal);
    }

    // A rede de segurança do navegador: ler o estado para as barras nunca deixa sair uma exceção para a thread da IU.
    [Fact]
    public void Service_ReadsTheStatusForTheBars_WithoutThrowing()
    {
        Assert.Equal(7, SyncService.Guarded(() => 7, 0));
        Assert.Equal(-1, SyncService.Guarded<int>(() => throw new InvalidOperationException("Collection was modified"), -1));
        Assert.Null(SyncService.Guarded<SyncStatus?>(() => throw new IOException("disco"), null));
        var ran = false;
        SyncService.Guarded(() => throw new IOException("disco"));
        SyncService.Guarded(() => ran = true);
        Assert.True(ran);

        var src = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Sync", "SyncService.cs"));
        Assert.DoesNotContain("Expired.Observe(agent.Status.NeedsSignIn)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_agent?.Status.NeedsSignIn == true; }", src, StringComparison.Ordinal);
        Assert.DoesNotContain("var status = agent.Status;", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_agent?.SnoozeKit(RecoveryReminder.SnoozeUntil(Now()));", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_agent?.DeclineKit();", src, StringComparison.Ordinal);
    }
}
