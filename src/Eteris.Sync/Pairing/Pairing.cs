using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Transport;

namespace Eteris.Sync.Pairing;

// Emparelhamento: um aparelho já ligado (ANFITRIÃO) entrega o segredo de conta a um aparelho novo (CONVIDADO) sem que
// o segredo apareça em claro em lado nenhum — nem no QR, nem no transporte (Google Drive / pasta).
//
//   QR (ou código curto) no ecrã do anfitrião:  sessão (80 bits) + chave pública efémera do anfitrião (P-256)
//   1 anfitrião → transporte : chave pública do anfitrião
//   2 convidado → transporte : chave pública do convidado + compromisso H(pkC ‖ pkA ‖ nC)
//   3 anfitrião → transporte : nonce nA (só depois de ver o compromisso)
//   4 convidado → transporte : nonce nC (abre o compromisso)
//   ambos mostram o código de verificação SAS = 6 algarismos de H(sessão ‖ pkA ‖ pkC ‖ nA ‖ nC)
//   o utilizador confirma NO ANFITRIÃO que os dois ecrãs mostram o mesmo código
//   5 anfitrião → transporte : segredo de conta cifrado com ECDH(pkA, pkC) → HKDF → AES-256-GCM (transcrito como AAD)
//
// Porque é seguro (comparação numérica, como no Bluetooth): o QR só tem dados públicos — fotografá-lo não dá nada.
// Quem também controlar o transporte e se meter no meio tem de escolher a sua chave e o seu nonce ANTES de ver o nonce
// do anfitrião (o compromisso), por isso acerta no código de 6 algarismos com probabilidade 1 em 1 000 000, e o
// utilizador vê códigos diferentes e recusa. Sem a confirmação no anfitrião, o passo 5 nunca é escrito.
// A sessão expira em 10 minutos e os ficheiros são apagados no fim (ou ao cancelar).
public static class PairingProtocol
{
    public const int Version = 1;
    public const string QrPrefix = "eteris-par:";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal const string HostKey = "1-host";
    internal const string GuestKey = "2-guest";
    internal const string HostNonce = "3-host";
    internal const string GuestNonce = "4-guest";
    internal const string Sealed = "5-host";
    internal const string Refused = "x-host";

    internal static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    internal static byte[] PublicKey(ECDiffieHellman key) => key.PublicKey.ExportSubjectPublicKeyInfo();

    internal static ECDiffieHellman ImportPublic(byte[] spki)
    {
        var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(spki, out var read);
        if (read != spki.Length || key.KeySize != 256) throw new CryptographicException("Chave pública inválida");
        return key;
    }

    internal static byte[] Commit(byte[] guestKey, byte[] hostKey, byte[] guestNonce)
        => SHA256.HashData(Concat("eteris-pair-commit-v1"u8.ToArray(), guestKey, hostKey, guestNonce));

    internal static byte[] Transcript(string session, byte[] hostKey, byte[] guestKey, byte[] hostNonce, byte[] guestNonce)
        => SHA256.HashData(Concat("eteris-pair-v1"u8.ToArray(), Encoding.ASCII.GetBytes(session), hostKey, guestKey, hostNonce, guestNonce));

    // Código de verificação: 6 algarismos em dois grupos («482 913»).
    internal static string Sas(byte[] transcript)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Concat("eteris-pair-sas-v1"u8.ToArray(), transcript))) % 1_000_000;
        var digits = value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return digits[..3] + " " + digits[3..];
    }

    internal static byte[] SessionKey(ECDiffieHellman mine, byte[] otherPublic, string session, byte[] transcript)
    {
        using var other = ImportPublic(otherPublic);
        var shared = mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
        try { return Kdf.Derive(shared, "eteris-pair-key-v1:" + Kdf.Hex(transcript), Encoding.ASCII.GetBytes(session)); }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    internal static byte[] Concat(params byte[][] parts)
    {
        var output = new byte[parts.Sum(p => p.Length + 4)];
        var offset = 0;
        foreach (var part in parts)
        {
            // Com o comprimento antes de cada parte: «ab|c» e «a|bc» dão resultados diferentes.
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(offset), part.Length);
            part.CopyTo(output, offset + 4);
            offset += part.Length + 4;
        }
        return output;
    }

    // Texto do QR: «eteris-par:1:<sessão>:<chave pública do anfitrião>». Nada que abra a conta.
    public static string QrText(string session, byte[] hostKey) => $"{QrPrefix}{Version}:{session}:{Kdf.Base64Url(hostKey)}";

    // Lê o QR ou o código curto (só a sessão, «K7Q2-9M3F-0RTA-WX5B»; a chave pública vem então do transporte e quem a
    // garante é o código de verificação).
    public static (string Session, byte[]? HostKey) ParseInvitation(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith(QrPrefix, StringComparison.Ordinal))
        {
            var parts = trimmed[QrPrefix.Length..].Split(':');
            if (parts.Length != 3 || parts[0] != Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new FormatException("Convite de emparelhamento de outra versão do Éteris.");
            return (NormalizeSession(parts[1]), Kdf.FromBase64Url(parts[2]));
        }
        return (NormalizeSession(trimmed), null);
    }

    internal static string NewSession() => RecoveryCode.ToBase32(RandomNumberGenerator.GetBytes(10));

    public static string ShortCode(string session) => string.Join('-', Enumerable.Range(0, session.Length / 4).Select(i => session.Substring(i * 4, 4)));

    private static string NormalizeSession(string text)
    {
        var s = RecoveryCode.Normalize(text);
        if (s.Length != 16 || !s.All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c))) throw new FormatException("Código de emparelhamento inválido.");
        return s;
    }
}

public sealed class PairingException(string message) : Exception(message);

// Mensagens (JSON, dentro de SealedFile só o passo 5; os outros são públicos por natureza).
internal sealed record PairingMessage
{
    [JsonPropertyName("v")] public int Version { get; init; } = PairingProtocol.Version;
    [JsonPropertyName("key")] public string? Key { get; init; }
    [JsonPropertyName("commit")] public string? Commit { get; init; }
    [JsonPropertyName("nonce")] public string? Nonce { get; init; }
    [JsonPropertyName("created")] public long CreatedMs { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

internal sealed record PairingSecret
{
    [JsonPropertyName("secret")] public required string Secret { get; init; }
    [JsonPropertyName("host")] public string HostName { get; init; } = string.Empty;
}

public abstract class PairingSide : IDisposable
{
    private protected readonly ISyncTransport Transport;
    private protected readonly Func<long> Now;
    private protected readonly Func<TimeSpan, CancellationToken, Task> Delay;
    private protected readonly ECDiffieHellman Key = PairingProtocol.NewKey();

    private protected PairingSide(ISyncTransport transport, string session, Func<long> now, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        Transport = transport;
        Session = session;
        Now = now;
        Delay = delay ?? Task.Delay;
    }

    public string Session { get; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    private protected byte[] PublicKey => PairingProtocol.PublicKey(Key);

    private protected string Path(string step) => SyncPaths.Pairing(Session) + step;

    private protected Task WriteAsync(string step, PairingMessage message, CancellationToken cancellationToken)
        => Transport.WriteAsync(Path(step), Json.Bytes(message), cancellationToken);

    private protected async Task<PairingMessage?> TryReadAsync(string step, CancellationToken cancellationToken)
    {
        var bytes = await Transport.ReadAsync(Path(step), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Json.Parse<PairingMessage>(bytes);
    }

    private protected async Task<PairingMessage> WaitAsync(string step, long deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await Transport.ReadAsync(Path(PairingProtocol.Refused), cancellationToken).ConfigureAwait(false) is not null)
                throw new PairingException("O outro aparelho recusou ou cancelou o emparelhamento.");
            if (await TryReadAsync(step, cancellationToken).ConfigureAwait(false) is { } message) return message;
            if (Now() > deadline) throw new PairingException("O emparelhamento expirou. Comece de novo.");
            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => Key.Dispose();
}

// Aparelho já ligado: mostra o QR, espera pelo convidado, mostra o código e só entrega o segredo depois de o
// utilizador confirmar que os códigos são iguais.
public sealed class PairingHost : PairingSide
{
    private readonly long _deadline;
    private readonly byte[] _hostNonce = RandomNumberGenerator.GetBytes(32);
    private byte[]? _guestKey;
    private byte[]? _transcript;

    private PairingHost(ISyncTransport transport, string session, Func<long> now, Func<TimeSpan, CancellationToken, Task>? delay)
        : base(transport, session, now, delay) => _deadline = now() + (long)PairingProtocol.Lifetime.TotalMilliseconds;

    public string QrText => PairingProtocol.QrText(Session, PublicKey);

    public string ShortCode => PairingProtocol.ShortCode(Session);

    public string? GuestName { get; private set; }

    public static async Task<PairingHost> StartAsync(ISyncTransport transport, Func<long> now, Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        var host = new PairingHost(transport, PairingProtocol.NewSession(), now, delay);
        await host.WriteAsync(PairingProtocol.HostKey, new PairingMessage { Key = Kdf.Base64Url(host.PublicKey), CreatedMs = now() }, cancellationToken).ConfigureAwait(false);
        return host;
    }

    // Espera pelo convidado e devolve o código de verificação para mostrar.
    public async Task<string> WaitForGuestAsync(CancellationToken cancellationToken = default)
    {
        var offer = await WaitAsync(PairingProtocol.GuestKey, _deadline, cancellationToken).ConfigureAwait(false);
        _guestKey = Kdf.FromBase64Url(offer.Key ?? throw new PairingException("Mensagem do convidado incompleta."));
        using (PairingProtocol.ImportPublic(_guestKey)) { }
        var commit = Kdf.FromBase64Url(offer.Commit ?? throw new PairingException("Mensagem do convidado incompleta."));
        GuestName = offer.Name;
        // O nonce do anfitrião só sai depois de o convidado se ter comprometido com a sua chave e o seu nonce.
        await WriteAsync(PairingProtocol.HostNonce, new PairingMessage { Nonce = Kdf.Base64Url(_hostNonce), CreatedMs = Now() }, cancellationToken).ConfigureAwait(false);
        var reveal = await WaitAsync(PairingProtocol.GuestNonce, _deadline, cancellationToken).ConfigureAwait(false);
        var guestNonce = Kdf.FromBase64Url(reveal.Nonce ?? throw new PairingException("Mensagem do convidado incompleta."));
        if (!CryptographicOperations.FixedTimeEquals(PairingProtocol.Commit(_guestKey, PublicKey, guestNonce), commit))
        {
            await RefuseAsync(cancellationToken).ConfigureAwait(false);
            throw new PairingException("O outro aparelho não cumpriu o que prometeu: emparelhamento recusado.");
        }
        _transcript = PairingProtocol.Transcript(Session, PublicKey, _guestKey, _hostNonce, guestNonce);
        return PairingProtocol.Sas(_transcript);
    }

    // O utilizador confirmou que o código é igual nos dois ecrãs: entrega o segredo, cifrado só para o convidado.
    public async Task ApproveAsync(AccountSecret secret, string hostName, CancellationToken cancellationToken = default)
    {
        if (_guestKey is null || _transcript is null) throw new InvalidOperationException("Ainda não há código de verificação.");
        if (Now() > _deadline) throw new PairingException("O emparelhamento expirou. Comece de novo.");
        var key = PairingProtocol.SessionKey(Key, _guestKey, Session, _transcript);
        var payload = Json.Bytes(new PairingSecret { Secret = Kdf.Base64Url(secret.ToBytes()), HostName = hostName });
        var sealedFile = SealedFile.Seal(key, SealedFile.Pairing, "pair", 1, Session + "|" + Kdf.Hex(_transcript), payload);
        CryptographicOperations.ZeroMemory(key);
        await Transport.WriteAsync(Path(PairingProtocol.Sealed), sealedFile, cancellationToken).ConfigureAwait(false);
    }

    public Task RefuseAsync(CancellationToken cancellationToken = default)
        => WriteAsync(PairingProtocol.Refused, new PairingMessage { CreatedMs = Now() }, cancellationToken);

    // Apaga a sessão do transporte (no fim, ao cancelar, ou ao expirar).
    public async Task CleanUpAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in await Transport.ListAsync(SyncPaths.Pairing(Session), cancellationToken).ConfigureAwait(false))
            await Transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
    }
}

// Aparelho novo: lê o QR (ou o código curto), mostra o mesmo código de verificação e recebe o segredo.
public sealed class PairingGuest : PairingSide
{
    private readonly byte[] _guestNonce = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _hostKey;
    private readonly long _deadline;
    private byte[]? _transcript;

    private PairingGuest(ISyncTransport transport, string session, byte[] hostKey, long deadline, Func<long> now, Func<TimeSpan, CancellationToken, Task>? delay)
        : base(transport, session, now, delay)
    {
        _hostKey = hostKey;
        _deadline = deadline;
    }

    public string? HostName { get; private set; }

    public static async Task<PairingGuest> JoinAsync(ISyncTransport transport, string invitation, string guestName, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken cancellationToken = default)
    {
        var (session, qrKey) = PairingProtocol.ParseInvitation(invitation);
        var bytes = await transport.ReadAsync(SyncPaths.Pairing(session) + PairingProtocol.HostKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new PairingException("Não encontrei este emparelhamento: confirme que os dois aparelhos usam a mesma conta Google (ou a mesma pasta).");
        var offer = Json.Parse<PairingMessage>(bytes);
        var hostKey = Kdf.FromBase64Url(offer.Key ?? throw new PairingException("Convite incompleto."));
        // Com QR, a chave do anfitrião vem do ecrã dele (autêntica); o transporte tem de dizer o mesmo.
        if (qrKey is not null && !CryptographicOperations.FixedTimeEquals(qrKey, hostKey))
            throw new PairingException("A chave do outro aparelho não confere com o QR: emparelhamento recusado.");
        if (now() - offer.CreatedMs > (long)PairingProtocol.Lifetime.TotalMilliseconds) throw new PairingException("O emparelhamento expirou. Comece de novo.");
        using (PairingProtocol.ImportPublic(hostKey)) { }
        var guest = new PairingGuest(transport, session, hostKey, offer.CreatedMs + (long)PairingProtocol.Lifetime.TotalMilliseconds, now, delay);
        var commit = PairingProtocol.Commit(guest.PublicKey, hostKey, guest._guestNonce);
        await guest.WriteAsync(PairingProtocol.GuestKey,
            new PairingMessage { Key = Kdf.Base64Url(guest.PublicKey), Commit = Kdf.Base64Url(commit), CreatedMs = now(), Name = guestName },
            cancellationToken).ConfigureAwait(false);
        return guest;
    }

    // Espera pelo nonce do anfitrião, revela o seu e devolve o código de verificação para mostrar.
    public async Task<string> WaitForCodeAsync(CancellationToken cancellationToken = default)
    {
        var hostNonce = Kdf.FromBase64Url((await WaitAsync(PairingProtocol.HostNonce, _deadline, cancellationToken).ConfigureAwait(false)).Nonce
                                          ?? throw new PairingException("Mensagem do anfitrião incompleta."));
        await WriteAsync(PairingProtocol.GuestNonce, new PairingMessage { Nonce = Kdf.Base64Url(_guestNonce), CreatedMs = Now() }, cancellationToken).ConfigureAwait(false);
        _transcript = PairingProtocol.Transcript(Session, _hostKey, PublicKey, hostNonce, _guestNonce);
        return PairingProtocol.Sas(_transcript);
    }

    // Espera que o utilizador confirme no anfitrião e devolve o segredo de conta.
    public async Task<AccountSecret> WaitForSecretAsync(CancellationToken cancellationToken = default)
    {
        if (_transcript is null) throw new InvalidOperationException("Ainda não há código de verificação.");
        while (true)
        {
            if (await Transport.ReadAsync(Path(PairingProtocol.Refused), cancellationToken).ConfigureAwait(false) is not null)
                throw new PairingException("O outro aparelho recusou ou cancelou o emparelhamento.");
            var bytes = await Transport.ReadAsync(Path(PairingProtocol.Sealed), cancellationToken).ConfigureAwait(false);
            if (bytes is not null)
            {
                var key = PairingProtocol.SessionKey(Key, _hostKey, Session, _transcript);
                try
                {
                    var payload = Json.Parse<PairingSecret>(SealedFile.Open(key, SealedFile.Pairing, "pair", Session + "|" + Kdf.Hex(_transcript), bytes));
                    HostName = payload.HostName;
                    return AccountSecret.FromBytes(Kdf.FromBase64Url(payload.Secret));
                }
                catch (CryptographicException)
                {
                    throw new PairingException("A resposta do outro aparelho não confere (alguém se meteu no meio?): emparelhamento recusado.");
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            if (Now() > _deadline) throw new PairingException("O emparelhamento expirou. Comece de novo.");
            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
