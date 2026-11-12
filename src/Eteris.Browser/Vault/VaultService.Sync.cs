using System.Text.Json.Nodes;
using Eteris.Sync.Secrets;

namespace Eteris.Browser.Vault;

// O que mudou ao aplicar um item vindo de outro aparelho: se era uma palavra-passe que mudou (a anterior ficou no
// histórico) e de que site.
public sealed record VaultSyncApplied(bool PasswordReplaced, string? Site);

// O cofre visto pela sincronização (D3; Vault/VaultSyncProfile). Cada item tem uma chave local e uma impressão
// (VaultStore.SyncKey/SyncPrint), gravadas sempre que o item é escrito: a sincronização compara sem decifrar nada.
public sealed partial class VaultService
{
    private (string Key, string Print) SyncOf(VaultCollection collection, string id, JsonObject meta, JsonObject? secret)
        => SyncOf(collection, VaultSyncMap.ToSync(collection, id, meta, secret));

    private (string Key, string Print) SyncOf(VaultCollection collection, SecretItem item)
    {
        var name = VaultCollections.Name(collection);
        return (Store.SyncKey(name, item.NaturalKey), Store.SyncPrint(name, SecretSchemas.Canonical(name, item.Data)));
    }

    // Chave local e impressão de cada item vivo (só disco; os itens de antes da sincronização ganham-nas agora, uma vez).
    public IReadOnlyList<(string Key, string Print)> SyncEntries(VaultCollection collection)
    {
        var result = new List<(string, string)>();
        foreach (var (id, key, print) in Store.SyncEntries(collection))
        {
            if (key is not null && print is not null)
            {
                result.Add((key, print));
                continue;
            }
            if (ReadSynced(collection, id, byId: true) is not { } item) continue;
            var sync = SyncOf(collection, item);
            Store.SetSync(collection, id, sync);
            result.Add(sync);
        }
        return result;
    }

    // O conteúdo de um item (pela chave local), decifrado agora: só quando a impressão mudou.
    public SecretItem? ReadSynced(VaultCollection collection, string localKey) => ReadSynced(collection, localKey, byId: false);

    private SecretItem? ReadSynced(VaultCollection collection, string key, bool byId)
    {
        var id = byId ? key : Store.FindBySyncKey(collection, key);
        if (id is null || Store.Get(collection, id) is not { } entry) return null;
        var secret = entry.HasSecret ? Store.ReadSecret(collection, id) : null;
        return VaultSyncMap.ToSync(collection, id, entry.Meta, secret);
    }

    public string SyncLocalKey(VaultCollection collection, string naturalKey) => Store.SyncKey(VaultCollections.Name(collection), naturalKey);

    public string SyncFingerprint(VaultCollection collection, SecretItem item)
    {
        var name = VaultCollections.Name(collection);
        return Store.SyncPrint(name, SecretSchemas.Canonical(name, item.Data));
    }

    // Grava um item vindo de outro aparelho, cifrado com a chave deste cofre, com os campos tal como vieram. Uma
    // palavra-passe que muda deixa a anterior no histórico local (1 versão). null = não é aceitável (fica de fora).
    public VaultSyncApplied? ApplySynced(VaultCollection collection, SecretItem item)
    {
        if (VaultSyncMap.FromSync(collection, item) is not { } stored) return null;
        var existing = Store.FindBySyncKey(collection, SyncLocalKey(collection, item.NaturalKey));
        var id = stored.FixedId ?? existing ?? VaultIds.New();
        // Um ID fixo (cartões, contactos…) que já é de outro item com outra chave: não se sobrepõe.
        if (existing is null && Store.Get(collection, id) is not null) return null;
        // A utilização mais recente fica: a «última utilização» não conta para a impressão, e a de aqui pode ser posterior à
        // do item recebido (preencheu-se antes de a ronda trazer a alteração).
        if (existing is not null && Store.Get(collection, existing) is { } local && Text(local.Meta, SecretSchemas.LastUsedField) is { } used
            && (Text(stored.Meta, SecretSchemas.LastUsedField) is not { } received || string.CompareOrdinal(used, received) > 0))
            stored.Meta[SecretSchemas.LastUsedField] = used;
        var replaced = false;
        if (collection == VaultCollection.Logins && existing is not null && Store.ReadSecret(collection, existing) is { } before)
            replaced = Text(before, "password") != Text(stored.Secret!, "password");
        Store.Put(collection, id, stored.IndexOrigin, stored.Meta, stored.Secret, Now, keepPrevious: replaced, sync: SyncOf(collection, item));
        var origin = Text(stored.Meta, "origin");
        return new VaultSyncApplied(replaced, origin is null ? null : Origins.DisplayHost(origin) ?? origin);
    }

    // Um item apagado noutro aparelho (pela chave local). true se já não está cá.
    public bool RemoveSynced(VaultCollection collection, string localKey)
    {
        if (Store.FindBySyncKey(collection, localKey) is not { } id) return true;
        Store.Delete(collection, id, Now);
        // A lápide do cofre só servia para a sincronização, que já a aplicou: sai de vez.
        Store.Purge(collection, id);
        return true;
    }
}
