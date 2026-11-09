using System.Security.Cryptography;
using Eteris.Sync.Crypto;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;

namespace Eteris.Sync.Format;

// Um item do cofre dentro (ou a sair) de um ficheiro exportado com segredos.
public sealed record ArchiveSecret(string Collection, SecretItem Item)
{
    public override string ToString() => $"ArchiveSecret {Collection}";
}

// Perfil exportado para um ficheiro .eteris (sem nuvem nenhuma): os registos do perfil cifrados com um código de
// recuperação novo, mostrado uma vez. Mesmo formato dos lotes (SealedFile, tipo P), com um sal aleatório público por
// ficheiro: dois ficheiros do mesmo perfil não partilham chave.
//
// Sem segredos por omissão. Com eles (a caixa «Incluir palavras-passe, pagamentos e documentos», depois do Windows
// Hello), cada item do cofre vai como um registo secreto selado (Secrets/SecretSeal) com chaves derivadas da chave do
// ficheiro: nem dentro do ficheiro aberto há um site ou um segredo em claro fora dos selos.
public static class ProfileArchive
{
    public const string Extension = ".eteris";
    private const string Chain = "archive";

    public static byte[] Export(IReadOnlyList<SyncRecord> records, AccountSecret secret, string deviceName, long nowMs, IReadOnlyList<ArchiveSecret>? secrets = null)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var all = records.Where(r => !Collections.IsSecret(r.Collection)).ToList();
        if (secrets is { Count: > 0 })
        {
            var seals = secret.ArchiveSeals(salt);
            foreach (var s in secrets)
            {
                if (!Collections.IsSecret(s.Collection)) throw new ArgumentException("Só coleções do cofre vão seladas.", nameof(secrets));
                var key = seals.OpaqueKey(s.Collection, s.Item.NaturalKey);
                var id = IdOf(s.Collection, key);
                all.Add(new SyncRecord { Collection = s.Collection, Id = id, Key = key, Version = new Hlc(nowMs, 0, "export"), Data = seals.Seal(s.Collection, id, s.Item) });
            }
        }
        var payload = new ArchivePayload { CreatedMs = nowMs, DeviceName = deviceName, Records = all };
        return SealedFile.Seal(secret.ArchiveKey(salt), SealedFile.Archive, Chain, 1, Context(salt), Json.Bytes(payload), salt,
            compress: !Collections.HasTabs(records));
    }

    // Lança FormatException (não é um perfil do Éteris), WrongCodeException (código errado ou ficheiro alterado) ou
    // UnsupportedFormatException (feito por um Éteris mais recente).
    public static ArchivePayload Import(ReadOnlySpan<byte> file, string code) => ImportAll(file, code).Payload;

    // O ficheiro e os itens do cofre que traz (abertos). Um selo que não abre fica de fora.
    public static (ArchivePayload Payload, IReadOnlyList<ArchiveSecret> Secrets) ImportAll(ReadOnlySpan<byte> file, string code)
    {
        var header = SealedFile.ReadHeader(file);
        if (header.Kind != SealedFile.Archive) throw new FormatException("Este ficheiro não é um perfil exportado do Éteris.");
        if (header.Public.Length != 16) throw new FormatException("Perfil exportado danificado.");
        using var secret = AccountSecret.FromCode(code);
        ArchivePayload payload;
        try
        {
            payload = Json.Parse<ArchivePayload>(SealedFile.Open(secret.ArchiveKey(header.Public), SealedFile.Archive, Chain, Context(header.Public), file));
        }
        catch (CryptographicException)
        {
            throw new WrongCodeException("O código não abre este ficheiro (é de outra exportação, ou o ficheiro foi alterado).");
        }
        var secrets = new List<ArchiveSecret>();
        if (payload.Records.Any(r => Collections.IsSecret(r.Collection)))
        {
            var seals = secret.ArchiveSeals(header.Public);
            foreach (var record in payload.Records.Where(r => Collections.IsSecret(r.Collection) && !r.Deleted))
            {
                try
                {
                    var item = seals.Open(record.Collection, record.Id, record.Data);
                    if (seals.OpaqueKey(record.Collection, item.NaturalKey) == record.Key && record.Id == IdOf(record.Collection, record.Key))
                        secrets.Add(new ArchiveSecret(record.Collection, item));
                }
                catch (CryptographicException) { }
            }
        }
        return (payload with { Records = payload.Records.Where(r => !Collections.IsSecret(r.Collection)).ToList() }, secrets);
    }

    private static string IdOf(string collection, string key) => Kdf.Hex(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(collection + "\n" + key)).AsSpan(0, 16));

    // Registo para um ficheiro exportado (sem cadeia não há chave de IDs: o ID é só um resumo da chave natural).
    public static SyncRecord Record(string collection, string key, System.Text.Json.Nodes.JsonObject data, long nowMs)
        => new()
        {
            Collection = collection,
            Id = IdOf(collection, key),
            Key = key,
            Version = new Hlc(nowMs, 0, "export"),
            Data = data,
        };

    private static string Context(ReadOnlySpan<byte> salt) => "archive/" + Kdf.Hex(salt);
}
