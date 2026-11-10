using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eteris.Sync.Secrets;

namespace Eteris.Sync.Crypto;

// O segredo de conta R (256 bits do gerador do sistema; nunca derivado de uma senha). Dele saem, por HKDF:
//   KEK    = HKDF(R, "kek")       — embrulha a chave de dados (DEK);
//   cadeia = HKDF(R, "chain-id")  — nome opaco da pasta da conta no transporte (16 bytes em hex).
// R só sai do aparelho escrito no kit de recuperação (24 palavras ou o código antigo) ou cifrado para outro aparelho
// (emparelhamento).
public sealed class AccountSecret : IDisposable
{
    private readonly byte[] _bytes;

    private AccountSecret(byte[] bytes) => _bytes = bytes;

    public static AccountSecret Create() => new(RandomNumberGenerator.GetBytes(RecoveryCode.SecretBytes));

    public static AccountSecret FromBytes(ReadOnlySpan<byte> bytes)
        => bytes.Length == RecoveryCode.SecretBytes ? new(bytes.ToArray()) : throw new ArgumentException("Segredo com tamanho errado");

    public static AccountSecret FromCode(string code) => new(RecoveryCode.Decode(code));

    public string ToCode() => RecoveryCode.Encode(_bytes);

    public string ToWords() => RecoveryCode.EncodeWords(_bytes);

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public string ChainId => Kdf.Hex(Kdf.Derive(_bytes, "chain-id", 16));

    internal byte[] Kek => Kdf.Derive(_bytes, "kek");

    // Chave de um ficheiro de perfil exportado (cada ficheiro tem o seu sal aleatório).
    internal byte[] ArchiveKey(ReadOnlySpan<byte> salt) => Kdf.Derive(_bytes, "profile-archive", salt);

    // Os selos dos registos secretos dentro de um ficheiro exportado (com segredos): derivados da chave do ficheiro,
    // como os da conta derivam da DEK (o kit de recuperação — 24 palavras ou o código antigo — abre o ficheiro e os selos).
    internal SealKeys ArchiveSeals(ReadOnlySpan<byte> salt)
    {
        var key = ArchiveKey(salt);
        try { return new SealKeys("archive/" + Kdf.Hex(salt), 1, Kdf.Base64Url(Kdf.Derive(key, "secrets:v1")), Kdf.Base64Url(Kdf.Derive(key, "secret-keys:v1"))); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
}

// Registo «chaves» guardado no transporte: só o que não é segredo fica em claro (formato, cadeia, geração, data) e a
// DEK aleatória vai embrulhada pela KEK (AES-256-GCM, com a cadeia e a geração como dados associados).
// Trocar a DEK (nova geração) não muda o código; trocar o código é «nova cadeia» (SyncEngine.RotateChainAsync).
public sealed record KeyEnvelope(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("chain")] string ChainId,
    [property: JsonPropertyName("generation")] int Generation,
    [property: JsonPropertyName("dek")] string WrappedDek,
    [property: JsonPropertyName("created")] long CreatedMs)
{
    public const int CurrentFormat = 1;

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static KeyEnvelope Parse(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize<KeyEnvelope>(json) ?? throw new FormatException("Registo de chaves vazio"); }
        catch (JsonException e) { throw new FormatException("Registo de chaves ilegível", e); }
    }
}

// O código não abre esta cadeia (é de outra conta, ou o registo de chaves foi adulterado).
public sealed class WrongCodeException(string message) : CryptographicException(message);

// Um ficheiro escrito por um Éteris mais recente: não se lê, não se marca como lido e pede-se a atualização.
public sealed class UnsupportedFormatException(string message) : Exception(message);

// Chaves de uma cadeia aberta: a DEK e as derivadas dela (uma por ficheiro, e a dos IDs opacos).
public sealed class KeyRing
{
    private readonly byte[] _dek;
    private readonly byte[] _ids;
    private readonly byte[] _kek;

    private KeyRing(KeyEnvelope envelope, byte[] dek, byte[] kek)
    {
        Envelope = envelope;
        _dek = dek;
        _kek = kek;
        _ids = Kdf.Derive(dek, "ids");
        SecretKey = Kdf.Derive(dek, "secrets:v1");
        SecretKeyId = Kdf.Derive(dek, "secret-keys:v1");
        Seals = new SealKeys(envelope.ChainId, envelope.Generation, Kdf.Base64Url(SecretKey), Kdf.Base64Url(SecretKeyId));
    }

    // Chaves dos registos secretos (Secrets/SecretSeal): K_seg cifra o selo, K_skey faz as chaves opacas. Derivam da DEK,
    // por isso qualquer aparelho da conta as tem, e mudam com a geração e com a «nova cadeia».
    internal byte[] SecretKey { get; }

    internal byte[] SecretKeyId { get; }

    public SealKeys Seals { get; }

    public string ChainId => Envelope.ChainId;

    public int Generation => Envelope.Generation;

    public KeyEnvelope Envelope { get; }

    public static KeyRing Create(AccountSecret secret, long nowMs, int generation = 1)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var chain = secret.ChainId;
        var kek = secret.Kek;
        var envelope = new KeyEnvelope(KeyEnvelope.CurrentFormat, chain, generation, Kdf.Base64Url(Wrap(kek, dek, chain, generation, KeyEnvelope.CurrentFormat)), nowMs);
        return new KeyRing(envelope, dek, kek);
    }

    // O formato do registo de chaves é AUTENTICADO: vai nos dados associados do embrulho da DEK («v<formato>»). Um
    // formato mais recente só pede «atualize o Éteris» se o embrulho abrir com esse formato (foi um aparelho da conta
    // que o escreveu); mudado por quem controla o transporte, é «registo alterado». Regra para os formatos futuros:
    // o embrulho da DEK continua AES-256-GCM com estes dados associados.
    public static KeyRing Open(AccountSecret secret, KeyEnvelope envelope)
    {
        if (!string.Equals(envelope.ChainId, secret.ChainId, StringComparison.Ordinal))
            throw new WrongCodeException("Este código é de outra conta de sincronização.");
        var kek = secret.Kek;
        byte[] dek;
        try
        {
            dek = Unwrap(kek, Kdf.FromBase64Url(envelope.WrappedDek), envelope.ChainId, envelope.Generation, envelope.Format);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new WrongCodeException("O código não abre esta conta (ou o registo de chaves foi alterado).");
        }
        if (envelope.Format > KeyEnvelope.CurrentFormat)
        {
            CryptographicOperations.ZeroMemory(dek);
            throw new UnsupportedFormatException($"Registo de chaves no formato {envelope.Format}: atualize o Éteris.");
        }
        return new KeyRing(envelope, dek, kek);
    }

    // O registo de chaves (lido de novo do transporte) foi mesmo escrito por um aparelho desta conta?
    internal bool Authenticates(KeyEnvelope envelope)
    {
        if (!string.Equals(envelope.ChainId, ChainId, StringComparison.Ordinal)) return false;
        try
        {
            CryptographicOperations.ZeroMemory(Unwrap(_kek, Kdf.FromBase64Url(envelope.WrappedDek), envelope.ChainId, envelope.Generation, envelope.Format));
            return true;
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return false; }
    }

    // Chave de um ficheiro (lote, instantâneo, estado), derivada do nome lógico: um ficheiro mudado de nome ou de lugar
    // deixa de se decifrar. NÃO é «uma cifra por chave»: o estado do aparelho (a/<aparelho>) é recifrado com a mesma
    // chave a cada Pull, e um lote que falhou o envio é recifrado igual. A segurança do AES-GCM vem do nonce aleatório
    // de 96 bits com poucas cifras por chave (milhares, longe das 2^32 do limite); um nonce derivado ou contador
    // exigiria outra solução (por exemplo um contador no contexto).
    internal byte[] FileKey(char kind, string context) => Kdf.Derive(_dek, $"file:{kind}:{context}");

    // ID opaco e estável de um registo: HMAC(K_ids, coleção ‖ chave natural), 128 bits. O mesmo URL dá o mesmo ID em
    // todos os aparelhos da conta, e nada o liga ao URL sem a chave.
    public string OpaqueId(string collection, string naturalKey)
        => Kdf.Hex(HMACSHA256.HashData(_ids, Encoding.UTF8.GetBytes(collection + "\n" + naturalKey)).AsSpan(0, 16));

    private static byte[] Aad(string chain, int generation, int format) => Encoding.UTF8.GetBytes($"eteris-keys|v{format}|{chain}|{generation}");

    // Só para os testes: um registo de chaves de um formato futuro, escrito por quem tem o código.
    internal static KeyEnvelope EnvelopeForFormat(AccountSecret secret, int format, long nowMs)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        return new KeyEnvelope(format, secret.ChainId, 1, Kdf.Base64Url(Wrap(secret.Kek, dek, secret.ChainId, 1, format)), nowMs);
    }

    private static byte[] Wrap(byte[] kek, byte[] dek, string chain, int generation, int format)
    {
        var output = new byte[12 + dek.Length + 16];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(output.AsSpan(0, 12), dek, output.AsSpan(12, dek.Length), output.AsSpan(12 + dek.Length), Aad(chain, generation, format));
        return output;
    }

    private static byte[] Unwrap(byte[] kek, byte[] wrapped, string chain, int generation, int format)
    {
        if (wrapped.Length != 12 + 32 + 16) throw new CryptographicException("Chave embrulhada com tamanho errado");
        var dek = new byte[32];
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(12, 32), wrapped.AsSpan(44, 16), dek, Aad(chain, generation, format));
        return dek;
    }
}
