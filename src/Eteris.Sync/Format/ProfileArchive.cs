using System.Security.Cryptography;
using Eteris.Sync.Crypto;
using Eteris.Sync.Model;

namespace Eteris.Sync.Format;

// Perfil exportado para um ficheiro .eteris (sem nuvem nenhuma): os registos do perfil cifrados com um código de
// recuperação novo, mostrado uma vez. Mesmo formato dos lotes (SealedFile, tipo P), com um sal aleatório público por
// ficheiro: dois ficheiros do mesmo perfil não partilham chave.
public static class ProfileArchive
{
    public const string Extension = ".eteris";
    private const string Chain = "archive";

    public static byte[] Export(IReadOnlyList<SyncRecord> records, AccountSecret secret, string deviceName, long nowMs)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var payload = new ArchivePayload { CreatedMs = nowMs, DeviceName = deviceName, Records = records };
        return SealedFile.Seal(secret.ArchiveKey(salt), SealedFile.Archive, Chain, 1, Context(salt), Json.Bytes(payload), salt,
            compress: !Collections.HasTabs(records));
    }

    // Lança FormatException (não é um perfil do Éteris), WrongCodeException (código errado ou ficheiro alterado) ou
    // UnsupportedFormatException (feito por um Éteris mais recente).
    public static ArchivePayload Import(ReadOnlySpan<byte> file, string code)
    {
        var header = SealedFile.ReadHeader(file);
        if (header.Kind != SealedFile.Archive) throw new FormatException("Este ficheiro não é um perfil exportado do Éteris.");
        if (header.Public.Length != 16) throw new FormatException("Perfil exportado danificado.");
        using var secret = AccountSecret.FromCode(code);
        try
        {
            return Json.Parse<ArchivePayload>(SealedFile.Open(secret.ArchiveKey(header.Public), SealedFile.Archive, Chain, Context(header.Public), file));
        }
        catch (CryptographicException)
        {
            throw new WrongCodeException("O código não abre este ficheiro (é de outra exportação, ou o ficheiro foi alterado).");
        }
    }

    // Registo para um ficheiro exportado (sem cadeia não há chave de IDs: o ID é só um resumo da chave natural).
    public static SyncRecord Record(string collection, string key, System.Text.Json.Nodes.JsonObject data, long nowMs)
        => new()
        {
            Collection = collection,
            Id = Kdf.Hex(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(collection + "\n" + key)).AsSpan(0, 16)),
            Key = key,
            Version = new Hlc(nowMs, 0, "export"),
            Data = data,
        };

    private static string Context(ReadOnlySpan<byte> salt) => "archive/" + Kdf.Hex(salt);
}
