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
//   4 convidado → transporte : nonce nC (abre o compromisso) + o nome do convidado CIFRADO com a chave da sessão
//   os dois calculam o código de verificação SAS = 6 algarismos de H(sessão ‖ pkA ‖ pkC ‖ nA ‖ nC); o código nunca
//   viaja. Nos pedidos dos aparelhos novos (PairingRequests, 0.2.15) só o CONVIDADO o mostra: a pessoa escreve-o no
//   anfitrião, que o compara com o seu sem o mostrar (SyncAgent.ApproveWithCodeAsync; 3 erros recusam o pedido)
//   5 anfitrião → transporte : segredo de conta cifrado com ECDH(pkA, pkC) → HKDF → AES-256-GCM (transcrito como AAD)
//   o utilizador confirma TAMBÉM NO CONVIDADO (ConfirmCode) antes de o segredo ser aberto e usado
//
// Porque é seguro (comparação numérica, como no Bluetooth): o QR só tem dados públicos — fotografá-lo não dá nada.
// Quem também controlar o transporte e se meter no meio tem de escolher a sua chave e o seu nonce ANTES de ver o nonce
// do anfitrião (o compromisso), por isso acerta no código de 6 algarismos com probabilidade 1 em 1 000 000, e o código
// que a pessoa escreve no anfitrião não bate. Sem o código certo no anfitrião, o passo 5 nunca é escrito.
// A confirmação no CONVIDADO é obrigatória também: com o código curto (sem QR), a chave do anfitrião vem do transporte,
// e quem o controla pode fazer de anfitrião só para o convidado (escrever logo o seu passo 5, com a SUA conta). O
// convidado só abre o segredo depois de o utilizador dizer que o código no ecrã dele é igual ao do anfitrião verdadeiro.
// Nada do utilizador vai em claro no transporte: o nome do aparelho convidado segue cifrado no passo 4.
// A sessão expira em 10 minutos e os ficheiros são apagados no fim (ou ao cancelar).
public static class PairingProtocol
{
    public const int Version = 1;
    public const string QrPrefix = "eteris-par:";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // Quanto à frente do relógio de quem responde pode estar a hora de um pedido (0-request). A hora é escolhida por
    // quem escreve o ficheiro: um pedido «do futuro» ficaria à espera mais do que os 10 minutos. O prazo de um pedido
    // conta-se sempre pelo relógio de quem responde, desde que respondeu.
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    internal const string Request = "0-request";
    internal const string HostKey = "1-host";
    internal const string GuestKey = "2-guest";
    internal const string HostNonce = "3-host";
    internal const string GuestNonce = "4-guest";
    internal const string Sealed = "5-host";
    internal const string Refused = "x-host";

    // Quantas vezes se tenta a escrita de um passo (sempre a MESMA mensagem) quando o transporte falha de passagem.
    internal const int WriteAttempts = 3;

    // Uma falha do transporte que passa sozinha (a rede, a pasta, o limite de tempo do cliente HTTP). Durante as esperas
    // do emparelhamento não acaba com o pedido: espera-se o intervalo e tenta-se outra vez, até ao prazo de sempre (o
    // prazo continua a ser a única saída por tempo). Não é uma falha destas: parar o pedido («stopped», o token dele,
    // cancelado), a autorização da Google por renovar (NeedsSignIn: não passa sozinha) e as mensagens que não conferem
    // (FormatException, CryptographicException: essas acabam com o pedido e contam).
    public static bool IsTransportFailure(Exception e, CancellationToken stopped)
        => e is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException
            or Google.OAuthException { NeedsSignIn: false }
           || (e is OperationCanceledException && !stopped.IsCancellationRequested);

    // A escrita de um passo: a mesma mensagem, até WriteAttempts vezes, com o intervalo da espera entre elas.
    internal static async Task WriteAsync(ISyncTransport transport, string path, byte[] data, Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan interval, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await transport.WriteAsync(path, data, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (attempt < WriteAttempts && IsTransportFailure(e, cancellationToken))
            {
                await delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    internal static byte[] PublicKey(ECDiffieHellman key) => key.PublicKey.ExportSubjectPublicKeyInfo();

    private const string P256Oid = "1.2.840.10045.3.1.7";

    // Só P-256 (secp256r1): outra curva de 256 bits (brainpoolP256r1, por exemplo) é recusada aqui, e não mais tarde
    // com uma exceção que ninguém trata.
    internal static ECDiffieHellman ImportPublic(byte[] spki)
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            var curve = key.ExportParameters(false).Curve;
            var isP256 = curve.IsNamed && (curve.Oid.Value == P256Oid || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "ECDH_P256" or "secp256r1");
            if (read != spki.Length || key.KeySize != 256 || !isP256) throw new CryptographicException("Curva errada");
            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new PairingException("A chave do outro aparelho não é válida: emparelhamento recusado.");
        }
    }

    // Nome do aparelho convidado: cifrado com a chave da sessão (a Google, ou quem tenha a pasta, não o lê).
    internal const int MaxNameLength = 80;

    internal static string NameContext(string session, byte[] transcript) => session + "|" + Kdf.Hex(transcript) + "|guest-name";

    internal static string SecretContext(string session, byte[] transcript) => session + "|" + Kdf.Hex(transcript);

    // A que anfitrião o convidado responde (impressão digital da chave dele, que já é pública no passo 1): com dois
    // aparelhos ligados a responder ao mesmo pedido, só aquele cuja chave o convidado usou continua; o outro desiste
    // calado, sem escrever a recusa (que fazia falhar o emparelhamento certo).
    internal static string HostTag(byte[] hostKey) => Kdf.Base64Url(SHA256.HashData(Concat("eteris-pair-host-v1"u8.ToArray(), hostKey)).AsSpan(0, 16).ToArray());

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

// Porque é que um emparelhamento acabou: o prazo, a recusa do outro lado, ou outra coisa (mensagens que não conferem,
// incompletas…). As mensagens e o tipo da exceção não mudam: quem precisa de distinguir lê Failure.
public enum PairingFailure { Other, TimedOut, Refused }

public class PairingException(string message) : Exception(message)
{
    public PairingFailure Failure { get; init; }
}

// Mensagens (JSON). Em claro só o que é público por natureza (chaves públicas, compromisso, nonces, hora); o nome do
// convidado (passo 4) e o segredo (passo 5) vão em SealedFile, cifrados com a chave da sessão.
internal sealed record PairingMessage
{
    [JsonPropertyName("v")] public int Version { get; init; } = PairingProtocol.Version;
    [JsonPropertyName("key")] public string? Key { get; init; }
    [JsonPropertyName("commit")] public string? Commit { get; init; }
    [JsonPropertyName("nonce")] public string? Nonce { get; init; }
    [JsonPropertyName("created")] public long CreatedMs { get; init; }
    [JsonPropertyName("sealedName")] public string? SealedName { get; init; }
    // Passo 2: a que chave de anfitrião o convidado se comprometeu (PairingProtocol.HostTag).
    [JsonPropertyName("for")] public string? For { get; init; }
}

// Outro aparelho ligado respondeu ao mesmo pedido e o convidado escolheu-o: este desiste sem recusar nada.
public sealed class PairingAnsweredElsewhereException() : PairingException("Outro aparelho respondeu a este pedido.");

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
        => PairingProtocol.WriteAsync(Transport, Path(step), Json.Bytes(message), Delay, PollInterval, cancellationToken);

    private protected async Task<PairingMessage?> TryReadAsync(string step, CancellationToken cancellationToken)
    {
        var bytes = await Transport.ReadAsync(Path(step), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Json.Parse<PairingMessage>(bytes);
    }

    private protected async Task<PairingMessage> WaitAsync(string step, long deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                if (await Transport.ReadAsync(Path(PairingProtocol.Refused), cancellationToken).ConfigureAwait(false) is not null)
                    throw new PairingException("O outro aparelho recusou ou cancelou o emparelhamento.") { Failure = PairingFailure.Refused };
                if (await TryReadAsync(step, cancellationToken).ConfigureAwait(false) is { } message) return message;
            }
            // O transporte falhou de passagem: a espera continua (a mesma leitura, no intervalo seguinte).
            catch (Exception e) when (PairingProtocol.IsTransportFailure(e, cancellationToken)) { }
            if (Now() > deadline) throw new PairingException("O emparelhamento expirou. Comece de novo.") { Failure = PairingFailure.TimedOut };
            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => Key.Dispose();
}

// Aparelho já ligado: responde ao pedido (ou mostra o QR), espera pelo convidado, calcula o código e só entrega o
// segredo depois de o utilizador o confirmar (nos pedidos dos aparelhos novos: escrevendo aqui o código que o
// convidado mostra).
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

    // Nome do convidado, conhecido só depois do passo 4 (vem cifrado). Mostrar ao lado do código de verificação.
    public string? GuestName { get; private set; }

    public static Task<PairingHost> StartAsync(ISyncTransport transport, Func<long> now, Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
        => StartAsync(transport, PairingProtocol.NewSession(), now, delay, cancellationToken);

    // Responde a um pedido aberto por um aparelho novo (PairingRequests): o anfitrião entra na sessão que o convidado
    // abriu. O resto do protocolo é igual (código de verificação, confirmação dos dois lados).
    public static Task<PairingHost> AnswerAsync(ISyncTransport transport, string session, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken cancellationToken = default)
        => StartAsync(transport, session, now, delay, cancellationToken);

    // Com uma sessão dada: pedidos dos aparelhos novos (AnswerAsync) e os testes (um atacante que controla o transporte
    // faz de anfitrião na sessão de outro).
    internal static async Task<PairingHost> StartAsync(ISyncTransport transport, string session, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken cancellationToken)
    {
        var host = new PairingHost(transport, session, now, delay);
        await host.WriteAsync(PairingProtocol.HostKey, new PairingMessage { Key = Kdf.Base64Url(host.PublicKey), CreatedMs = now() }, cancellationToken).ConfigureAwait(false);
        return host;
    }

    // Espera pelo convidado e devolve o código de verificação (para comparar com o que a pessoa escrever).
    public async Task<string> WaitForGuestAsync(CancellationToken cancellationToken = default)
    {
        var offer = await WaitAsync(PairingProtocol.GuestKey, _deadline, cancellationToken).ConfigureAwait(false);
        // Dirigida a outro anfitrião (outro aparelho ligado respondeu ao mesmo pedido): sai sem escrever nada.
        if (offer.For is { } target && target != PairingProtocol.HostTag(PublicKey)) throw new PairingAnsweredElsewhereException();
        _guestKey = Kdf.FromBase64Url(offer.Key ?? throw new PairingException("Mensagem do convidado incompleta."));
        using (PairingProtocol.ImportPublic(_guestKey)) { }
        var commit = Kdf.FromBase64Url(offer.Commit ?? throw new PairingException("Mensagem do convidado incompleta."));
        // O nonce do anfitrião só sai depois de o convidado se ter comprometido com a sua chave e o seu nonce.
        await WriteAsync(PairingProtocol.HostNonce, new PairingMessage { Nonce = Kdf.Base64Url(_hostNonce), CreatedMs = Now() }, cancellationToken).ConfigureAwait(false);
        var reveal = await WaitAsync(PairingProtocol.GuestNonce, _deadline, cancellationToken).ConfigureAwait(false);
        var guestNonce = Kdf.FromBase64Url(reveal.Nonce ?? throw new PairingException("Mensagem do convidado incompleta."));
        if (!CryptographicOperations.FixedTimeEquals(PairingProtocol.Commit(_guestKey, PublicKey, guestNonce), commit))
        {
            await RefuseAsync(cancellationToken).ConfigureAwait(false);
            throw new PairingException("O outro aparelho não cumpriu o que prometeu: emparelhamento recusado.");
        }
        var transcript = PairingProtocol.Transcript(Session, PublicKey, _guestKey, _hostNonce, guestNonce);
        if (reveal.SealedName is { } sealedName)
        {
            var key = PairingProtocol.SessionKey(Key, _guestKey, Session, transcript);
            try
            {
                var name = Encoding.UTF8.GetString(SealedFile.Open(key, SealedFile.Pairing, "pair", PairingProtocol.NameContext(Session, transcript),
                    Kdf.FromBase64Url(sealedName)));
                GuestName = name.Length > PairingProtocol.MaxNameLength ? name[..PairingProtocol.MaxNameLength] : name;
            }
            catch (Exception e) when (e is CryptographicException or FormatException or UnsupportedFormatException)
            {
                await RefuseAsync(cancellationToken).ConfigureAwait(false);
                throw new PairingException("A mensagem do outro aparelho não confere: emparelhamento recusado.");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        _transcript = transcript;
        return PairingProtocol.Sas(_transcript);
    }

    // O utilizador confirmou o código (escreveu aqui o que o convidado mostra, e bateu): entrega o segredo, cifrado só
    // para o convidado.
    public async Task ApproveAsync(AccountSecret secret, string hostName, CancellationToken cancellationToken = default)
    {
        if (_guestKey is null || _transcript is null) throw new InvalidOperationException("Ainda não há código de verificação.");
        if (Now() > _deadline) throw new PairingException("O emparelhamento expirou. Comece de novo.") { Failure = PairingFailure.TimedOut };
        var key = PairingProtocol.SessionKey(Key, _guestKey, Session, _transcript);
        var payload = Json.Bytes(new PairingSecret { Secret = Kdf.Base64Url(secret.ToBytes()), HostName = hostName });
        var sealedFile = SealedFile.Seal(key, SealedFile.Pairing, "pair", 1, PairingProtocol.SecretContext(Session, _transcript), payload);
        CryptographicOperations.ZeroMemory(key);
        await PairingProtocol.WriteAsync(Transport, Path(PairingProtocol.Sealed), sealedFile, Delay, PollInterval, cancellationToken).ConfigureAwait(false);
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

// Aparelho novo: lê o QR (ou o código curto, ou segue o seu pedido), mostra o código de verificação (que a pessoa
// escreve no anfitrião) e, DEPOIS de o utilizador confirmar também aqui (ConfirmCode), recebe o segredo.
public sealed class PairingGuest : PairingSide
{
    private readonly byte[] _guestNonce = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _hostKey;
    private readonly long _deadline;
    private readonly string _name;
    private byte[]? _transcript;
    private bool _confirmed;

    private PairingGuest(ISyncTransport transport, string session, byte[] hostKey, long deadline, string name, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay)
        : base(transport, session, now, delay)
    {
        _hostKey = hostKey;
        _deadline = deadline;
        _name = name.Length > PairingProtocol.MaxNameLength ? name[..PairingProtocol.MaxNameLength] : name;
    }

    public string? HostName { get; private set; }

    public static async Task<PairingGuest> JoinAsync(ISyncTransport transport, string invitation, string guestName, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay = null, CancellationToken cancellationToken = default)
    {
        var (session, qrKey) = PairingProtocol.ParseInvitation(invitation);
        var bytes = await transport.ReadAsync(SyncPaths.Pairing(session) + PairingProtocol.HostKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new PairingException("Não encontrei este emparelhamento: confirme que os dois aparelhos usam a mesma conta Google (ou a mesma pasta).");
        return await JoinAsync(transport, session, qrKey, bytes, guestName, now, delay, cancellationToken).ConfigureAwait(false);
    }

    // Com a resposta do anfitrião (passo 1) já lida: quem espera pela resposta (PairingRequests.WaitForHostAsync) lê-a
    // no seu ciclo, onde uma falha de leitura não acaba com o pedido; daqui em diante só há a escrita do passo 2.
    internal static async Task<PairingGuest> JoinAsync(ISyncTransport transport, string session, byte[]? qrKey, byte[] bytes, string guestName,
        Func<long> now, Func<TimeSpan, CancellationToken, Task>? delay, CancellationToken cancellationToken)
    {
        var offer = Json.Parse<PairingMessage>(bytes);
        var hostKey = Kdf.FromBase64Url(offer.Key ?? throw new PairingException("Convite incompleto."));
        // Com QR, a chave do anfitrião vem do ecrã dele (autêntica); o transporte tem de dizer o mesmo.
        if (qrKey is not null && !CryptographicOperations.FixedTimeEquals(qrKey, hostKey))
            throw new PairingException("A chave do outro aparelho não confere com o QR: emparelhamento recusado.");
        if (now() - offer.CreatedMs > (long)PairingProtocol.Lifetime.TotalMilliseconds)
            throw new PairingException("O emparelhamento expirou. Comece de novo.") { Failure = PairingFailure.TimedOut };
        using (PairingProtocol.ImportPublic(hostKey)) { }
        var guest = new PairingGuest(transport, session, hostKey, offer.CreatedMs + (long)PairingProtocol.Lifetime.TotalMilliseconds, guestName, now, delay);
        var commit = PairingProtocol.Commit(guest.PublicKey, hostKey, guest._guestNonce);
        // Sem o nome: no passo 2 ainda não há chave da sessão, e o transporte não pode ler «PC da Ana».
        await guest.WriteAsync(PairingProtocol.GuestKey,
            new PairingMessage { Key = Kdf.Base64Url(guest.PublicKey), Commit = Kdf.Base64Url(commit), CreatedMs = now(), For = PairingProtocol.HostTag(hostKey) },
            cancellationToken).ConfigureAwait(false);
        return guest;
    }

    // Espera pelo nonce do anfitrião, revela o seu e devolve o código de verificação para mostrar.
    public async Task<string> WaitForCodeAsync(CancellationToken cancellationToken = default)
    {
        var hostNonce = Kdf.FromBase64Url((await WaitAsync(PairingProtocol.HostNonce, _deadline, cancellationToken).ConfigureAwait(false)).Nonce
                                          ?? throw new PairingException("Mensagem do anfitrião incompleta."));
        var transcript = PairingProtocol.Transcript(Session, _hostKey, PublicKey, hostNonce, _guestNonce);
        // O nome segue cifrado com a chave da sessão, na mesma mensagem que abre o compromisso.
        var key = PairingProtocol.SessionKey(Key, _hostKey, Session, transcript);
        string sealedName;
        try
        {
            sealedName = Kdf.Base64Url(SealedFile.Seal(key, SealedFile.Pairing, "pair", 1, PairingProtocol.NameContext(Session, transcript),
                Encoding.UTF8.GetBytes(_name)));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        await WriteAsync(PairingProtocol.GuestNonce, new PairingMessage { Nonce = Kdf.Base64Url(_guestNonce), CreatedMs = Now(), SealedName = sealedName },
            cancellationToken).ConfigureAwait(false);
        _transcript = transcript;
        return PairingProtocol.Sas(_transcript);
    }

    // O utilizador confirmou NESTE aparelho que o código mostrado é igual ao do anfitrião. Obrigatório antes de
    // WaitForSecretAsync: sem isto, quem controla o transporte (código curto) podia entregar a SUA conta ao convidado.
    public void ConfirmCode()
    {
        if (_transcript is null) throw new InvalidOperationException("Ainda não há código de verificação.");
        _confirmed = true;
    }

    // Espera que o utilizador confirme no anfitrião e devolve o segredo de conta. Só depois de ConfirmCode.
    // A interface mostra depois o nome do anfitrião (HostName) e o código outra vez antes de juntar o aparelho à conta.
    public async Task<AccountSecret> WaitForSecretAsync(CancellationToken cancellationToken = default)
    {
        if (_transcript is null) throw new InvalidOperationException("Ainda não há código de verificação.");
        if (!_confirmed) throw new InvalidOperationException("O utilizador ainda não confirmou neste aparelho que os códigos são iguais.");
        while (true)
        {
            byte[]? bytes = null;
            try
            {
                if (await Transport.ReadAsync(Path(PairingProtocol.Refused), cancellationToken).ConfigureAwait(false) is not null)
                    throw new PairingException("O outro aparelho recusou ou cancelou o emparelhamento.") { Failure = PairingFailure.Refused };
                bytes = await Transport.ReadAsync(Path(PairingProtocol.Sealed), cancellationToken).ConfigureAwait(false);
            }
            // O transporte falhou de passagem: a espera continua.
            catch (Exception e) when (PairingProtocol.IsTransportFailure(e, cancellationToken)) { }
            if (bytes is not null)
            {
                var key = PairingProtocol.SessionKey(Key, _hostKey, Session, _transcript);
                try
                {
                    var payload = Json.Parse<PairingSecret>(SealedFile.Open(key, SealedFile.Pairing, "pair", PairingProtocol.SecretContext(Session, _transcript), bytes));
                    HostName = payload.HostName;
                    return AccountSecret.FromBytes(Kdf.FromBase64Url(payload.Secret));
                }
                // Não abre, não se lê, ou o segredo não tem o tamanho certo: tudo isto é «não confere» (quem escreve no
                // transporte consegue provocá-lo), nunca uma exceção que quem espera não trate.
                catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
                {
                    throw new PairingException("A resposta do outro aparelho não confere (alguém se meteu no meio?): emparelhamento recusado.");
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            if (Now() > _deadline) throw new PairingException("O emparelhamento expirou. Comece de novo.") { Failure = PairingFailure.TimedOut };
            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
