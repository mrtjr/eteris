using System.IO;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;

namespace Eteris.Browser.Vault;

// O cofre visto pela sincronização (D3; desenho, secção 4.3). Ler dá, de cada item das coleções ligadas, só a chave
// local e a impressão (nada se decifra); o conteúdo só sai por ReadSecret, item a item, quando a impressão mudou.
// Aplicar volta a cifrar com a chave deste cofre. O cofre só se abre quando é preciso: sem nenhuma coleção do cofre
// ligada, ou sem cofre neste perfil (nunca foi usado), a leitura não o abre nem o cria.
//
// Nunca sobe: o histórico local, os candidatos por guardar, a chave do cofre, a janela de graça, os «Pedir o Windows
// Hello…» (definições deste computador) — e a «última utilização» das janelas privadas, que nem chega a ser gravada (C7).
public sealed class VaultSyncProfile : ISecretSource, ILocalNotices
{
    private readonly Func<VaultService> _vault;
    private readonly Func<bool> _exists;
    private readonly Lock _gate = new();
    private string? _notice;

    // exists: há cofre neste perfil (para a leitura não criar um). Por omissão, o do perfil em uso (<raiz>\cofre.key).
    public VaultSyncProfile(Func<VaultService> vault, Func<bool>? exists = null)
    {
        _vault = vault;
        _exists = exists ?? (() => File.Exists(Path.Combine(Infrastructure.AppPaths.Root, VaultKey.FileName)));
    }

    private static VaultCollection CollectionOf(string name) => VaultCollections.Parse(name) ?? throw new ArgumentException("Coleção que não é do cofre.", nameof(name));

    // Os itens das coleções do cofre ligadas (null = todas), e as que não se leram (cofre que não abre nesta conta do
    // Windows, chave em falta, base de dados ocupada): «não consegui ler» nunca vira «apaguei tudo» nos outros aparelhos.
    public (List<LocalItem> Items, List<string> Unreadable) Read(IReadOnlySet<string>? enabled)
    {
        var items = new List<LocalItem>();
        var unreadable = new List<string>();
        var wanted = Collections.Secret.Where(c => enabled is null || enabled.Contains(c)).ToList();
        if (wanted.Count == 0) return (items, unreadable);
        bool exists;
        try { exists = _exists(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { exists = false; }
        if (!exists) return (items, unreadable);
        foreach (var name in wanted)
        {
            try
            {
                foreach (var (key, print) in _vault().SyncEntries(CollectionOf(name)))
                    items.Add(new LocalItem(name, key, ProfileSync.Print(print)));
            }
            catch (Exception e) when (IsVaultFailure(e))
            {
                items.RemoveAll(i => i.Collection == name);
                unreadable.Add(name);
            }
        }
        return (items, unreadable);
    }

    internal static bool IsVaultFailure(Exception e)
        => e is VaultUnavailableException or VaultCorruptException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
            or System.Security.Cryptography.CryptographicException or InvalidOperationException;

    public SecretItem? ReadSecret(string collection, string localKey)
    {
        try { return _vault().ReadSynced(CollectionOf(collection), localKey); }
        catch (Exception e) when (IsVaultFailure(e)) { return null; }
    }

    // Um cofre que não abre (chave em falta, DPAPI, base de dados ocupada ou estragada) faz a coleção ilegível nessa ronda
    // (SecretSourceUnavailableException), em vez de deitar abaixo a ronda inteira.
    public string LocalKey(string collection, string naturalKey)
    {
        try { return _vault().SyncLocalKey(CollectionOf(collection), naturalKey); }
        catch (Exception e) when (IsVaultFailure(e)) { throw new SecretSourceUnavailableException(collection, e); }
    }

    public string Fingerprint(string collection, SecretItem item)
    {
        try { return _vault().SyncFingerprint(CollectionOf(collection), item); }
        catch (Exception e) when (IsVaultFailure(e)) { throw new SecretSourceUnavailableException(collection, e); }
    }

    // Aplica as alterações das coleções do cofre e devolve as que aplicou.
    public IReadOnlyCollection<LocalChange> Apply(IEnumerable<LocalChange> changes)
    {
        var applied = new List<LocalChange>();
        var replaced = new List<string?>();
        foreach (var change in changes)
        {
            if (!Collections.IsSecret(change.Collection)) continue;
            try
            {
                var collection = CollectionOf(change.Collection);
                if (change.Remove)
                {
                    if (_vault().RemoveSynced(collection, change.Key)) applied.Add(change);
                    continue;
                }
                if (change.Secret is not { } item || _vault().SyncLocalKey(collection, item.NaturalKey) != change.Key) continue;
                if (_vault().ApplySynced(collection, item) is not { } result) continue;
                if (result.PasswordReplaced) replaced.Add(result.Site);
                applied.Add(change);
            }
            catch (Exception e) when (IsVaultFailure(e) || e is ArgumentException) { }
        }
        if (replaced.Count > 0)
        {
            var text = replaced.Count == 1
                ? $"A palavra-passe de {replaced[0] ?? "um site"} mudou noutro computador; a anterior ficou no histórico (Palavras-passe › o item › «Palavra-passe anterior»)."
                : $"{replaced.Count} palavras-passe mudaram noutro computador; as anteriores ficaram no histórico de cada uma.";
            lock (_gate) _notice = text;
        }
        return applied;
    }

    public string? TakeNotice()
    {
        lock (_gate)
        {
            var notice = _notice;
            _notice = null;
            return notice;
        }
    }
}

// O perfil do navegador inteiro para a sincronização: o resto do perfil (favoritos, definições, Escudo, Espaços, notas;
// Sync/BrowserProfile) e o cofre. As coleções do cofre só se leem quando estão ligadas neste aparelho.
public sealed class ProfileWithVault(ILocalProfileReader plain, VaultSyncProfile vault, Func<IReadOnlySet<string>?> enabled)
    : ILocalProfileReader, ISecretSource, ILocalNotices
{
    public LocalRead Read()
    {
        var rest = plain.Read();
        var (items, unreadable) = vault.Read(enabled());
        return new LocalRead([.. rest.Items.Where(i => !Collections.IsSecret(i.Collection)), .. items], [.. rest.Unreadable, .. unreadable]);
    }

    public IReadOnlyList<LocalItem> Collect() => Read().Items;

    public IReadOnlyCollection<LocalChange> Apply(IReadOnlyList<LocalChange> changes)
    {
        var plainChanges = changes.Where(c => !Collections.IsSecret(c.Collection)).ToList();
        var applied = new List<LocalChange>(plainChanges.Count == 0 ? [] : plain.Apply(plainChanges));
        applied.AddRange(vault.Apply(changes.Where(c => Collections.IsSecret(c.Collection))));
        return applied;
    }

    public SecretItem? ReadSecret(string collection, string localKey) => vault.ReadSecret(collection, localKey);

    public string LocalKey(string collection, string naturalKey) => vault.LocalKey(collection, naturalKey);

    public string Fingerprint(string collection, SecretItem item) => vault.Fingerprint(collection, item);

    public string? TakeNotice() => vault.TakeNotice();
}
