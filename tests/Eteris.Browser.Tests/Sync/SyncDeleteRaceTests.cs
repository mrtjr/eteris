using System.IO;
using System.Net.Http;
using System.Text.Json;
using Eteris.Sync.Engine;
using Eteris.Sync.Google;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// «Apagar os meus dados do Éteris no Google Drive» com o que a verificação independente encontrou: uma «cópia em
// conflito» na pasta, uma falha que não passa sozinha, o apagar a meio de uma entrada (código ou chave guardada) e
// «Ligar outra vez» depois de um apagar que não acabou.
public sealed class SyncDeleteRaceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-delete-race-" + Guid.NewGuid().ToString("N"));
    private long _now = 1_762_560_000_000;

    private long Now() => _now;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private List<string> Files()
        => Directory.Exists(_folder)
            ? Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList()
            : [];

    private SyncAgent Agent(MemoryProfile profile, MemoryStore store, string name = "Casa", Func<ISyncTransport>? transport = null)
        => new(transport ?? (() => new FolderTransport(_folder)), profile, store, name, Now);

    // ---- «cópias em conflito» ---------------------------------------------------------------------------------------

    // O cliente da nuvem deixou «keys (1)» e um lote «… (1)» na pasta: o apagar leva-os também (estão dentro da pasta
    // «eteris», são do Éteris), acaba, e «Ligar outra vez» cria uma conta nova.
    [Fact]
    public async Task Delete_WithAConflictCopyInTheFolder()
    {
        var profile = new MemoryProfile();
        profile.Bookmark("https://casa.example/", "Casa");
        var store = new MemoryStore();
        var casa = Agent(profile, store);
        await casa.StartAsync();
        var keys = Directory.EnumerateFiles(_folder, "keys", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(keys + " (1)", [1, 2, 3]);
        var other = Directory.EnumerateFiles(Path.GetDirectoryName(keys)!, "*", SearchOption.AllDirectories).First(f => Path.GetFileName(f) != "keys" && !f.EndsWith(" (1)", StringComparison.Ordinal));
        File.WriteAllBytes(other + " (Cópia em conflito de Trabalho)", [4, 5, 6]);

        await casa.DeleteCloudDataAsync();

        Assert.Empty(Files());
        var saved = store.Load()!;
        Assert.False(saved.DeletePending);
        Assert.False(saved.Enabled);
        Assert.False(casa.Status.Deleting);
        Assert.Null(casa.Status.DeleteError);
        Assert.Contains("apagados", casa.Status.Notice);

        await casa.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Status.Phase);
        Assert.NotEqual(KitState.Saved, casa.Status.Kit!.State);
        Assert.True(store.Load()!.Enabled);
    }

    // A pasta: os nomes que ela própria lista (também as cópias em conflito, dentro de «eteris») apagam-se; nada fora de
    // «eteris», nada com «..».
    [Fact]
    public async Task FolderTransport_DeletesConflictCopiesInsideItsFolder_AndNothingElse()
    {
        var transport = new FolderTransport(_folder);
        await transport.WriteAsync("eteris/0123456789abcdef0123456789abcdef/keys", [1]);
        var chain = Path.Combine(_folder, "eteris", "0123456789abcdef0123456789abcdef");
        File.WriteAllBytes(Path.Combine(chain, "keys (1)"), [2]);
        File.WriteAllBytes(Path.Combine(_folder, "fora (1)"), [3]);
        var listed = await transport.ListAsync(SyncPaths.Root);
        Assert.Contains("eteris/0123456789abcdef0123456789abcdef/keys (1)", listed);
        foreach (var path in listed) await transport.DeleteAsync(path);
        Assert.Equal(["fora (1)"], Files());
        await Assert.ThrowsAsync<ArgumentException>(() => transport.DeleteAsync("fora (1)"));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.DeleteAsync("eteris/../fora (1)"));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.DeleteAsync("eteris/x/..\\..\\fora (1)"));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.DeleteAsync("eteris/x:fluxo"));
        // Ler e escrever continuam só com os nomes do Éteris.
        await Assert.ThrowsAsync<ArgumentException>(() => transport.ReadAsync("eteris/0123456789abcdef0123456789abcdef/keys (1)"));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.WriteAsync("eteris/0123456789abcdef0123456789abcdef/keys (1)", [1]));
        Assert.Equal(["fora (1)"], Files());
    }

    // Um nome que o transporte recusa apagar (ArgumentException) não trava o apagar: fica de fora, o resto sai, o
    // apagar acaba e o aviso diz que ficou alguma coisa.
    [Fact]
    public async Task Delete_ANameTheTransportRefuses_IsLeftOut_AndTheDeleteFinishes()
    {
        var profile = new MemoryProfile();
        var store = new MemoryStore();
        var refusing = new FaultyTransport(new FolderTransport(_folder)) { RefuseDeleting = p => p.EndsWith("/keys", StringComparison.Ordinal) };
        var casa = Agent(profile, store, transport: () => refusing);
        await casa.StartAsync();
        await casa.DeleteCloudDataAsync();
        Assert.Single(Files());
        Assert.EndsWith("/keys", Files()[0], StringComparison.Ordinal);
        Assert.False(store.Load()!.DeletePending);
        Assert.False(casa.Status.Deleting);
        Assert.Contains("1 ficheiro", casa.Status.Notice);
    }

    // ---- falhas que não passam sozinhas -----------------------------------------------------------------------------

    // Uma falha inesperada (aqui, uma lista ilegível) chega ao utilizador: SyncUserException com o porquê, o estado diz
    // «não acabou» (DeleteError, sem «running»), e «Tentar de novo» (FinishCloudDeletionAsync) acaba quando a falha passa.
    [Fact]
    public async Task Delete_AnUnexpectedFailure_IsShown_AndTryingAgainFinishes()
    {
        var profile = new MemoryProfile();
        var store = new MemoryStore();
        var faulty = new FaultyTransport(new FolderTransport(_folder));
        var casa = Agent(profile, store, transport: () => faulty);
        await casa.StartAsync();
        faulty.ListFailure = () => new JsonException("lista ilegível");

        var e = await Assert.ThrowsAsync<SyncUserException>(() => casa.DeleteCloudDataAsync());
        Assert.Contains("Ainda não se conseguiu apagar", e.Message);
        Assert.Contains("Tentar de novo", e.Message);
        Assert.True(store.Load()!.DeletePending);
        Assert.False(store.Load()!.Enabled);
        Assert.True(casa.Status.Deleting);
        Assert.False(casa.Status.Running);
        Assert.Equal(e.Message, casa.Status.DeleteError);

        faulty.ListFailure = null;
        await casa.FinishCloudDeletionAsync();
        Assert.Empty(Files());
        Assert.False(casa.Status.Deleting);
        Assert.Null(casa.Status.DeleteError);
        Assert.False(store.Load()!.DeletePending);
    }

    // No Google Drive, uma lista que não é JSON (um portal de Wi-Fi, um proxy) é uma falha de rede: passa sozinha.
    [Fact]
    public async Task Drive_AGarbledList_IsANetworkFailure()
    {
        var google = new FakeGoogle();
        var http = GoogleEndpoints.CreateClient(google);
        var oauth = new GoogleOAuth(http, new OAuthClientConfig(google.ClientId, null), Now);
        var session = new GoogleSession(oauth, new TokenStore(Path.Combine(_folder, "tokens"), new PlainProtector()), Now);
        session.Use(new OAuthTokens(google.ValidAccessToken, google.RefreshToken, _now + 3_600_000, GoogleEndpoints.Scope));
        var drive = new GoogleDriveTransport(http, session.AccessTokenAsync, (_, _) => Task.CompletedTask);
        google.GarbledLists = 1;
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => drive.ListAsync(SyncPaths.Root));
        Assert.True(SyncAgent.IsTransient(e));
        Assert.Empty(await drive.ListAsync(SyncPaths.Root));
    }

    // «Ligar outra vez» com um apagar que falha: não fica bloqueado em silêncio — diz porquê (SyncUserException), e
    // «Ligar sem acabar de apagar» (AbandonCloudDeletion) liga mesmo assim; o que ficou na pasta fica lá.
    [Fact]
    public async Task Start_WithADeleteThatKeepsFailing_SaysWhy_AndCanStartAnyway()
    {
        var profile = new MemoryProfile();
        profile.Bookmark("https://casa.example/", "Casa");
        var store = new MemoryStore();
        var faulty = new FaultyTransport(new FolderTransport(_folder));
        var casa = Agent(profile, store, transport: () => faulty);
        await casa.StartAsync();
        faulty.DeleteFailure = () => new InvalidOperationException("estado inesperado");
        await Assert.ThrowsAsync<SyncUserException>(() => casa.DeleteCloudDataAsync());

        var e = await Assert.ThrowsAsync<SyncUserException>(() => casa.StartAsync());
        Assert.Contains("Ligar sem acabar de apagar", e.Message);
        Assert.False(store.Load()!.Enabled);
        Assert.True(casa.Status.Deleting);
        Assert.NotNull(casa.Status.DeleteError);

        var old = Files();
        Assert.NotEmpty(old);
        casa.AbandonCloudDeletion();
        Assert.False(casa.DeletePending);
        Assert.False(casa.Status.Deleting);
        Assert.Null(casa.Status.DeleteError);
        faulty.DeleteFailure = null;
        await casa.StartAsync();
        Assert.True(store.Load()!.Enabled);
        Assert.False(store.Load()!.DeletePending);
        // A conta antiga ainda lá está (o registo de chaves não saiu): pede para escolher como entrar, sem apagar nada.
        Assert.Equal(SyncPhase.Choose, casa.Status.Phase);
        Assert.Equal(old, Files());
    }

    // Sem rede: o apagar diz que não acabou, nenhuma ronda envia nada, «Ligar» sem rede também diz porquê, e com rede
    // «Ligar» acaba de apagar e cria uma conta nova (uma só cadeia, nova).
    [Fact]
    public async Task Delete_WithoutNetwork_ThenStart()
    {
        var faulty = new FaultyTransport(new FolderTransport(_folder));
        var profile = new MemoryProfile();
        profile.Bookmark("https://casa.example/", "Casa");
        var store = new MemoryStore();
        var casa = Agent(profile, store, transport: () => faulty);
        await casa.StartAsync();
        var before = Files();
        var oldChain = before[0].Split('/')[1];
        faulty.Down = true;
        await Assert.ThrowsAsync<SyncUserException>(() => casa.DeleteCloudDataAsync());
        var s = store.Load()!;
        Assert.False(s.Enabled);
        Assert.True(s.DeletePending);
        Assert.Null(s.Secret);
        Assert.NotNull(casa.Status.DeleteError);
        Assert.Equal(before, Files());

        profile.Bookmark("https://depois.example/", "Depois");
        Assert.True(await casa.RunAsync(force: true));
        Assert.Equal(before, Files());
        await Assert.ThrowsAsync<SyncUserException>(() => casa.StartAsync());
        Assert.False(store.Load()!.Enabled);
        Assert.True(store.Load()!.DeletePending);

        faulty.Down = false;
        await casa.StartAsync();
        Assert.Equal(SyncPhase.On, casa.Status.Phase);
        Assert.NotEqual(KitState.Saved, casa.Status.Kit!.State);
        Assert.False(store.Load()!.DeletePending);
        Assert.Null(casa.Status.DeleteError);
        var chains = Files().Select(f => f.Split('/')[1]).Where(c => c != "pair").Distinct().ToList();
        Assert.Single(chains);
        Assert.NotEqual(oldChain, chains[0]);
    }

    // ---- o apagar a meio de uma entrada -----------------------------------------------------------------------------

    // «Apagar…» enquanto este computador entra com o código: a entrada já não grava nada depois do apagar (nem a
    // chave, nem o motor, nem «ligada»), o apagar acaba, e nenhuma ronda volta a criar a conta.
    [Fact]
    public async Task Delete_DuringJoinWithCode()
    {
        var casaProfile = new MemoryProfile();
        casaProfile.Bookmark("https://casa.example/", "Casa");
        var casa = Agent(casaProfile, new MemoryStore());
        await casa.StartAsync();
        var code = casa.RevealKit()!.Words;

        var hook = new HookTransport(new FolderTransport(_folder));
        var profile = new MemoryProfile();
        profile.Bookmark("https://trabalho.example/", "Trabalho");
        var store = new MemoryStore();
        var trabalho = Agent(profile, store, "Trabalho", () => hook);
        await trabalho.StartAsync();
        Assert.Equal(SyncPhase.Choose, trabalho.Status.Phase);

        Task? finish = null;
        hook.OnRead = () =>
        {
            // O que a rota faz (SyncService.DeleteCloud): Begin já, Finish em segundo plano.
            trabalho.BeginCloudDeletion();
            finish = Task.Run(() => trabalho.FinishCloudDeletionAsync());
        };
        await trabalho.JoinWithCodeAsync(code);
        Assert.NotNull(finish);
        await finish!.WaitAsync(TimeSpan.FromSeconds(30));

        AssertDeletedAndOff(trabalho, store);
        profile.Bookmark("https://depois.example/", "Depois");
        Assert.True(await trabalho.RunAsync(force: true));
        Assert.Empty(Files());
        var reaberta = Agent(profile, store, "Trabalho");
        Assert.False(reaberta.Enabled);
        Assert.True(await reaberta.RunAsync(force: true));
        Assert.Empty(Files());
    }

    // O mesmo ao retomar com a chave guardada (SetUpAsync → JoinCoreAsync): depois do apagar, nada volta a gravar.
    [Fact]
    public async Task Delete_DuringSetUpWithTheKeptKey()
    {
        var profile = new MemoryProfile();
        profile.Bookmark("https://casa.example/", "Casa");
        var store = new MemoryStore();
        var casa = Agent(profile, store);
        await casa.StartAsync();
        casa.MarkKitSaved();
        // A chave ficou, o motor não (por exemplo, a conta parou com «a chave ficou neste computador»).
        var kept = store.Load()!;
        kept.Engine = null;
        kept.Enabled = false;
        store.Save(kept);

        var hook = new HookTransport(new FolderTransport(_folder));
        var aberta = Agent(profile, store, transport: () => hook);
        Task? finish = null;
        hook.OnRead = () =>
        {
            aberta.BeginCloudDeletion();
            finish = Task.Run(() => aberta.FinishCloudDeletionAsync());
        };
        await aberta.StartAsync();
        Assert.NotNull(finish);
        await finish!.WaitAsync(TimeSpan.FromSeconds(30));

        AssertDeletedAndOff(aberta, store);
        profile.Bookmark("https://depois.example/", "Depois");
        Assert.True(await aberta.RunAsync(force: true));
        Assert.Empty(Files());
    }

    // Outro computador a meio de uma ronda quando este apaga: a ronda dele acaba (ou falha) sem recriar a conta, e na
    // ronda seguinte para; a pasta fica vazia.
    [Fact]
    public async Task Delete_WhileOtherComputerIsMidRound()
    {
        var casaP = new MemoryProfile();
        casaP.Bookmark("https://casa.example/", "Casa");
        var casa = Agent(casaP, new MemoryStore());
        await casa.StartAsync();
        var trabP = new MemoryProfile();
        trabP.Bookmark("https://trab.example/", "Trab");
        var trab = Agent(trabP, new MemoryStore(), "Trabalho");
        await trab.StartAsync();
        await trab.JoinWithCodeAsync(casa.RevealKit()!.Words);
        await casa.RunAsync();
        await trab.RunAsync();
        await casa.RunAsync();
        trabP.Bookmark("https://nova.example/", "Nova");
        trabP.DuringCycle = () => casa.DeleteCloudDataAsync().GetAwaiter().GetResult();
        await trab.RunAsync();
        trabP.DuringCycle = null;
        await trab.RunAsync();
        Assert.Equal(SyncPhase.Stopped, trab.Status.Phase);
        await casa.FinishCloudDeletionAsync();
        Assert.True(await trab.RunAsync());
        Assert.Equal(SyncPhase.Stopped, trab.Status.Phase);
        // O que a ronda do outro escreveu depois do apagar fica (não é deste computador desfazer), mas nenhuma conta
        // voltou a abrir: sem registo de chaves, ninguém lá entra.
        Assert.DoesNotContain(Files(), f => f.EndsWith("/keys", StringComparison.Ordinal));
    }

    private void AssertDeletedAndOff(SyncAgent agent, MemoryStore store)
    {
        var saved = store.Load()!;
        Assert.False(saved.Enabled);
        Assert.False(saved.DeletePending);
        Assert.Null(saved.Secret);
        Assert.Null(saved.Engine);
        Assert.False(agent.Enabled);
        Assert.False(agent.Status.Deleting);
        Assert.Equal(SyncPhase.Stopped, agent.Status.Phase);
        Assert.Contains("apagados", agent.Status.Notice);
        Assert.Empty(Files());
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    // Chama OnRead uma vez, na primeira leitura depois de armado (a meio de entrar: o registo de chaves).
    private sealed class HookTransport(ISyncTransport inner) : ISyncTransport
    {
        public Action? OnRead;
        public string Name => inner.Name;
        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
        {
            Interlocked.Exchange(ref OnRead, null)?.Invoke();
            return inner.ReadAsync(path, ct);
        }

        public Task WriteAsync(string path, byte[] data, CancellationToken ct = default) => inner.WriteAsync(path, data, ct);
        public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
    }

    // Sem rede (IOException), ou com uma falha inesperada nas listas ou ao apagar, ou um nome que não se deixa apagar.
    private sealed class FaultyTransport(ISyncTransport inner) : ISyncTransport
    {
        public volatile bool Down;
        public Func<Exception>? ListFailure;
        public Func<Exception>? DeleteFailure;
        public Func<string, bool>? RefuseDeleting;
        public string Name => inner.Name;

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
            => Down ? throw new IOException("sem rede") : ListFailure is { } f ? throw f() : inner.ListAsync(prefix, ct);

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default) => Down ? throw new IOException("sem rede") : inner.ReadAsync(path, ct);

        public Task WriteAsync(string path, byte[] data, CancellationToken ct = default) => Down ? throw new IOException("sem rede") : inner.WriteAsync(path, data, ct);

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => Down ? throw new IOException("sem rede")
                : DeleteFailure is { } f ? throw f()
                : RefuseDeleting?.Invoke(path) == true ? throw new ArgumentException("Nome de ficheiro inválido: " + path)
                : inner.DeleteAsync(path, ct);
    }
}
