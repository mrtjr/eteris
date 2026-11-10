using System.IO;
using Eteris.Browser.Sync;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Google;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Apagar do disco o que tem segredos — o estado da sincronização (com a chave da conta) e os tokens da Google — quando
// o Windows não deixa apagar o ficheiro agora (preso por outro processo): revisão final da 0.2.15, A-09. O que a pessoa
// mandou apagar nunca volta a ser lido, mesmo que o ficheiro ainda lá esteja ao abrir outra vez. Só pastas temporárias.
public sealed class SecretFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-secret-file-" + Guid.NewGuid().ToString("N"));

    public SecretFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Plain : ISecretProtector
    {
        public byte[] Protect(byte[] data) => data;
        public byte[] Unprotect(byte[] data) => data;
    }

    private string StatePath => Path.Combine(_folder, "sync", "sync.state");

    private DpapiSyncStore NewStore() => new(StatePath, new Plain());

    private static SavedSync WithKey() => new() { Secret = "segredo-da-conta", Enabled = true };

    // O caso normal continua igual: o ficheiro sai, e o temporário de uma gravação interrompida também.
    [Fact]
    public void SyncState_Delete_RemovesTheFile_AndTheTemporaryOne()
    {
        var store = NewStore();
        store.Save(WithKey());
        File.WriteAllBytes(StatePath + ".tmp", [1, 2, 3]);
        store.Delete();
        Assert.Null(store.Load());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatePath)!));
    }

    // «Sair» com «apagar a chave» e o ficheiro preso em exclusivo por outro processo: o apagar não lança, e nem depois de
    // o outro processo o largar — nem ao abrir o Éteris outra vez — a chave volta.
    [Fact]
    public void SyncState_Delete_WithTheFileLockedByAnotherProcess_NeverGivesTheKeyBack()
    {
        var store = NewStore();
        store.Save(WithKey());
        using (new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Delete();
            Assert.Null(store.Load());
        }
        // O outro processo largou o ficheiro, que ainda lá está com a chave: quem lê acaba o apagar.
        Assert.True(File.Exists(StatePath));
        Assert.Null(NewStore().Load());
        Assert.False(File.Exists(StatePath));
        Assert.Null(store.Load());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatePath)!));
    }

    // Preso por um processo que deixa escrever (mas não apagar): o conteúdo sai já do disco.
    [Fact]
    public void SyncState_Delete_WithTheFileOpenElsewhere_EmptiesItAtOnce()
    {
        var store = NewStore();
        store.Save(WithKey());
        using (new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            store.Delete();
            Assert.Equal(0, new FileInfo(StatePath).Length);
            Assert.Null(store.Load());
        }
        Assert.Null(NewStore().Load());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatePath)!));
    }

    // Ligar outra vez depois de um apagar que ficou por fazer: o estado novo grava-se e lê-se (o apagar antigo não o leva).
    [Fact]
    public void SyncState_SaveAfterAPendingDelete_KeepsTheNewState()
    {
        var store = NewStore();
        store.Save(WithKey());
        using (new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None)) store.Delete();
        store.Save(new SavedSync { Secret = "segredo-novo", Enabled = true });
        Assert.Equal("segredo-novo", store.Load()!.Secret);
        Assert.Equal("segredo-novo", NewStore().Load()!.Secret);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StatePath)!));
    }

    // Nem apagar, nem esvaziar, nem deixar a marca: a falha chega a quem chamou (para a página o dizer).
    [Fact]
    public void SyncState_Delete_ThatCannotEvenLeaveAMark_Throws()
    {
        var store = NewStore();
        store.Save(WithKey());
        // Uma pasta com o nome da marca: a marca não se consegue escrever.
        Directory.CreateDirectory(SecretFile.PendingDeletePath(StatePath));
        using (new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(store.Delete);
    }

    // Os tokens da Google têm o mesmo cuidado: «Sair» com o ficheiro preso não os deixa voltar.
    [Fact]
    public void Tokens_Delete_WithTheFileLockedByAnotherProcess_NeverGivesThemBack()
    {
        var path = Path.Combine(_folder, "sync", "google.tokens");
        var store = new TokenStore(path, new Plain());
        store.Save(new OAuthTokens("acesso", "renovacao", 1_762_560_000_000, GoogleEndpoints.Scope));
        File.WriteAllBytes(path + ".tmp", [1]);
        Assert.Equal("renovacao", store.Load()!.RefreshToken);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Delete();
            Assert.Null(store.Load());
        }
        Assert.Null(new TokenStore(path, new Plain()).Load());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
        // Entrar outra vez grava os tokens novos.
        store.Save(new OAuthTokens("acesso-2", "renovacao-2", 1_762_560_000_000, GoogleEndpoints.Scope));
        Assert.Equal("renovacao-2", new TokenStore(path, new Plain()).Load()!.RefreshToken);
    }

    // No agente: se o estado não se apagou de maneira nenhuma, «Sair» acaba na mesma (a sincronização para aqui, os
    // pedidos a meio param, quem ouve é avisado) e só então diz que a chave ficou no disco.
    [Fact]
    public async Task SignOut_WhenTheStateCannotBeDeleted_StillSignsOut_AndSaysSo()
    {
        var store = new StubbornStore();
        var agent = new SyncAgent(() => new MemoryTransport(), new MemoryProfile(), store, "Casa", () => 1_762_560_000_000);
        await agent.StartAsync();
        Assert.True(agent.HasKey);
        var changed = 0;
        agent.Changed += () => changed++;
        store.FailDelete = true;
        var failed = Assert.Throws<SyncUserException>(() => agent.SignOut(forgetKey: true));
        Assert.Contains("não se conseguiu apagar", failed.Message);
        Assert.Equal(SyncPhase.Off, agent.Status.Phase);
        Assert.False(agent.Enabled);
        Assert.False(agent.HasKey);
        Assert.True(changed > 0);
        // No navegador, a rota de «Sair» segue para os tokens da Google mesmo assim.
        var service = GoogleSyncTests.Body(GoogleSyncTests.Src("Eteris.Browser", "Sync", "SyncService.cs"), "public static void SignOut(bool forgetKey)");
        Assert.Contains("catch (SyncUserException", service);
    }

    private sealed class StubbornStore : ISyncStore
    {
        private readonly MemoryStore _inner = new();

        public bool FailDelete { get; set; }

        public SavedSync? Load() => _inner.Load();

        public void Save(SavedSync saved) => _inner.Save(saved);

        public void Delete()
        {
            if (FailDelete) throw new IOException("preso por outro processo");
            _inner.Delete();
        }
    }
}
