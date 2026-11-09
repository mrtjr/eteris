using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Eteris.Sync.Crypto;

namespace Eteris.Sync.Secrets;

// O conteúdo de um registo secreto: a chave natural (origem e utilizador, ou o ID do item) e os campos.
public sealed record SecretItem(string NaturalKey, JsonObject Data)
{
    public override string ToString() => "SecretItem";
}

// Um selo que não abre (adulterado, movido para outro registo, de outra conta ou cadeia, ou de um formato mais recente).
public sealed class SecretSealException(string message, Exception? inner = null) : CryptographicException(message, inner);

// As chaves dos selos de uma cadeia (KeyRing.Seals): K_seg = HKDF(DEK, "secrets:v1") cifra, K_skey =
// HKDF(DEK, "secret-keys:v1") faz as chaves opacas. Mudam com a cadeia e com a geração. O motor guarda-as no seu
// estado (que o anfitrião protege com DPAPI e nunca vai para o transporte) só para voltar a selar os registos deste
// aparelho quando outro aparelho faz «nova cadeia» (SyncEngine.RestageOwn): a cadeia antiga já não existe nessa altura.
public sealed record SealKeys(
    [property: JsonPropertyName("chain")] string Chain,
    [property: JsonPropertyName("gen")] int Generation,
    [property: JsonPropertyName("seal")] string SealKey,
    [property: JsonPropertyName("ids")] string IdKey)
{
    // Chave opaca de um registo secreto no motor: "s:" + HMAC(K_skey, coleção ‖ chave natural), 128 bits. A mesma em
    // todos os aparelhos da conta; nada a liga ao site sem a chave.
    public string OpaqueKey(string collection, string naturalKey) => SecretSeal.OpaqueKey(Kdf.FromBase64Url(IdKey), collection, naturalKey);

    public JsonObject Seal(string collection, string id, SecretItem item) => SecretSeal.Seal(Kdf.FromBase64Url(SealKey), Chain, Generation, collection, id, item);

    public SecretItem Open(string collection, string id, JsonObject? sealedData) => SecretSeal.Open(Kdf.FromBase64Url(SealKey), Chain, Generation, collection, id, sealedData);

    public override string ToString() => $"SealKeys {Chain}/{Generation}";
}

// O selo de um registo secreto, por dentro da cifra do ficheiro (desenho, secção 4.2):
//   Data = {"v":1,"n":<nonce de 96 bits>,"c":<AES-256-GCM(K_seg, {"k":chave natural,"d":campos})>}
//   dados associados = eteris-secret|v1|<cadeia>|<geração>|<coleção>|<Id>
// O motor funde os registos sem nunca os abrir; os instantâneos, os lotes, a base do ProfileSync e o estado do motor
// nunca têm um segredo nem um site em claro. O bloco cifrado é incompressível: nenhum canal lateral de compressão ao
// lado de títulos que os sites escolhem. Um selo movido para outro Id, coleção, cadeia ou geração não abre.
public static class SecretSeal
{
    public const int Version = 1;

    // Um registo secreto não passa disto (uma nota enorme não faz um lote de vários MiB).
    public const int MaxBytes = 64 * 1024;

    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string OpaqueKey(ReadOnlySpan<byte> idKey, string collection, string naturalKey)
        => "s:" + Kdf.Hex(HMACSHA256.HashData(idKey, Encoding.UTF8.GetBytes(collection + "\n" + naturalKey)).AsSpan(0, 16));

    public static JsonObject Seal(ReadOnlySpan<byte> key, string chain, int generation, string collection, string id, SecretItem item)
    {
        SecretSchemas.RejectForbidden(item.Data);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["k"] = item.NaturalKey, ["d"] = item.Data.DeepClone() });
        try
        {
            if (plain.Length > MaxBytes) throw new ArgumentException($"Um registo secreto não pode passar de {MaxBytes / 1024} KiB.");
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var output = new byte[plain.Length + TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), Aad(chain, generation, collection, id));
            return new JsonObject { ["v"] = Version, ["n"] = Kdf.Base64Url(nonce), ["c"] = Kdf.Base64Url(output) };
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static SecretItem Open(ReadOnlySpan<byte> key, string chain, int generation, string collection, string id, JsonObject? sealedData)
    {
        if (sealedData is null || sealedData["v"] is not JsonValue v || !v.TryGetValue<int>(out var version))
            throw new SecretSealException("Registo secreto sem selo.");
        if (version != Version) throw new SecretSealException($"Selo na versão {version}: atualize o Éteris.");
        if (sealedData["n"] is not JsonValue n || !n.TryGetValue<string>(out var nonceText) || sealedData["c"] is not JsonValue c
            || !c.TryGetValue<string>(out var cipherText))
            throw new SecretSealException("Selo incompleto.");
        byte[] nonce, cipher;
        try
        {
            nonce = Kdf.FromBase64Url(nonceText);
            cipher = Kdf.FromBase64Url(cipherText);
        }
        catch (FormatException e) { throw new SecretSealException("Selo ilegível.", e); }
        if (nonce.Length != NonceSize || cipher.Length < TagSize || cipher.Length > MaxBytes + TagSize) throw new SecretSealException("Selo com tamanho errado.");
        var plain = new byte[cipher.Length - TagSize];
        try
        {
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain, Aad(chain, generation, collection, id));
            if (JsonNode.Parse(plain) is not JsonObject inner || inner["k"] is not JsonValue k || !k.TryGetValue<string>(out var natural)
                || inner["d"] is not JsonObject data)
                throw new SecretSealException("Selo sem chave ou sem campos.");
            return new SecretItem(natural, (JsonObject)data.DeepClone());
        }
        catch (CryptographicException e) when (e is not SecretSealException) { throw new SecretSealException("O selo não passa a verificação.", e); }
        catch (JsonException e) { throw new SecretSealException("Selo ilegível.", e); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Aad(string chain, int generation, string collection, string id)
        => Encoding.UTF8.GetBytes($"eteris-secret|v{Version}|{chain}|{generation}|{collection}|{id}");
}
