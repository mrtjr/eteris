using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Google;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Um erro passageiro do transporte durante a espera do emparelhamento (revisão final da 0.2.15, A-02): a rede cai um
// instante, o Drive responde tarde, a pasta não se lê. O pedido não morre: quem espera tenta outra vez no intervalo
// seguinte, até ao prazo de sempre (10 minutos), e as escritas dos passos repetem-se até 3 vezes. Só acabam com o
// pedido (e contam para os pedidos em série) as mensagens que não conferem. Tudo em memória, com esperas por sinal.
public sealed class PairingTransportFailureTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(1, ct);
    private long _now = 1_762_560_000_000;
    private readonly SyncTestCloud _cloud = new();

    private long Now() => _now;

    public void Dispose() => _cloud.StopAll();

    private sealed record Device(SyncAgent Agent, MemoryProfile Profile, MemoryStore Store, FaultyTransport Transport);

    private Device NewDevice(string name)
    {
        var profile = new MemoryProfile();
        var store = new MemoryStore();
        var transport = new FaultyTransport(_cloud, _cloud.Pulse);
        var agent = new SyncAgent(() => transport, profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(1) };
        _cloud.Watch(name, agent);
        return new Device(agent, profile, store, transport);
    }

    private static int BadCount(Device device) => device.Store.Load()?.BadPairings?.Length ?? 0;

    private static Exception Folder() => new IOException("pasta inacessível");

    // O limite de 60 s do cliente HTTP: uma TaskCanceledException com uma TimeoutException dentro.
    private static Exception HttpTimeout() => new TaskCanceledException("O pedido excedeu o limite de tempo.", new TimeoutException());

    // A Casa na conta e um computador novo pronto a pedir (ainda sem pedido).
    private async Task<(Device Casa, Device Novo)> TwoAsync()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Bookmark("https://pt.wikipedia.org/", "Wikipédia");
        await casa.Agent.StartAsync();
        casa.Agent.MarkKitSaved();
        var novo = NewDevice("Portátil");
        novo.Profile.Bookmark("https://portatil.example/", "Do portátil");
        await novo.Agent.StartAsync();
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        return (casa, novo);
    }

    // O pedido do computador novo, respondido pela Casa, até o código estar nos dois lados.
    private async Task<(string Session, string Code)> ReadyAsync(Device casa, Device novo)
    {
        novo.Agent.RequestPairing();
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 1, "o pedido no transporte");
        await casa.Agent.CheckRequestsAsync();
        await _cloud.WaitFor(() => casa.Agent.Status.Requests.Count == 1 && novo.Agent.Status.Pairing?.Code is not null, "o pedido pronto");
        return (casa.Agent.Status.Requests[0].Session, novo.Agent.Status.Pairing!.Code!);
    }

    private async Task FinishAsync(Device casa, Device novo, string session, string code)
    {
        novo.Agent.ConfirmPairing();
        Assert.Equal(PairingAnswer.Approved, await casa.Agent.ApproveWithCodeAsync(session, code));
        await _cloud.WaitFor(() => novo.Agent.Status is { Phase: SyncPhase.On, Running: false, JoinedNow: false }, "o portátil ligado");
        Assert.Null(novo.Agent.Status.Error);
        for (var round = 0; round < 3; round++)
        {
            Assert.True(await casa.Agent.RunAsync());
            Assert.True(await novo.Agent.RunAsync());
        }
        Assert.Equal("Do portátil", casa.Profile.Title("https://portatil.example/"));
        Assert.Equal("Wikipédia", novo.Profile.Title("https://pt.wikipedia.org/"));
        Assert.Equal(0, BadCount(casa));
        Assert.False(casa.Agent.Status.PairingQuiet);
    }

    // Duas falhas seguidas em cada ponto de espera ou de escrita, de cada lado: o emparelhamento acaba bem na mesma.
    [Theory]
    [InlineData("casa", "read", "/2-guest")]      // a Casa à espera do computador novo
    [InlineData("casa", "read", "/4-guest")]
    [InlineData("casa", "read", "/x-host")]
    [InlineData("casa", "write", "/1-host")]
    [InlineData("casa", "write", "/3-host")]
    [InlineData("casa", "write", "/5-host")]
    [InlineData("novo", "write", "/0-request")]
    [InlineData("novo", "read", "/1-host")]       // o computador novo à espera de uma resposta
    [InlineData("novo", "read", "/x-host")]
    [InlineData("novo", "write", "/2-guest")]
    [InlineData("novo", "read", "/3-host")]
    [InlineData("novo", "write", "/4-guest")]
    [InlineData("novo", "read", "/5-host")]       // à espera da entrega
    public async Task TwoTransientFailures_AtEachPoint_ThePairingStillEndsWell(string side, string op, string end)
    {
        var (casa, novo) = await TwoAsync();
        var faulty = side == "casa" ? casa.Transport : novo.Transport;
        faulty.Fail(op, end, times: 2, Folder);
        var (session, code) = await ReadyAsync(casa, novo);
        await FinishAsync(casa, novo, session, code);
        Assert.Equal(2, faulty.Thrown);
    }

    // O limite de tempo do cliente HTTP (não é o pedido a ser parado) também passa sozinho.
    [Theory]
    [InlineData("casa", "/2-guest")]
    [InlineData("novo", "/1-host")]
    [InlineData("novo", "/3-host")]
    [InlineData("novo", "/5-host")]
    public async Task HttpTimeouts_WhileWaiting_DoNotEndThePairing(string side, string end)
    {
        var (casa, novo) = await TwoAsync();
        var faulty = side == "casa" ? casa.Transport : novo.Transport;
        faulty.Fail("read", end, times: 2, HttpTimeout);
        var (session, code) = await ReadyAsync(casa, novo);
        await FinishAsync(casa, novo, session, code);
        Assert.Equal(2, faulty.Thrown);
    }

    // À espera de a pessoa dizer «Sim» no computador novo: o transporte falha várias vezes e o código continua à vista.
    [Fact]
    public async Task Failures_WhileWaitingForTheConfirmation_KeepTheCodeOnScreen()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        novo.Transport.Fail("read", "/x-host", times: 5, Folder);
        await _cloud.WaitFor(() => novo.Transport.Left("read", "/x-host") == 0, "as cinco falhas");
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);
        Assert.Equal(code, novo.Agent.Status.Pairing!.Code);
        Assert.Null(novo.Agent.Status.Pairing.Error);
        await FinishAsync(casa, novo, session, code);
    }

    // A limpeza dos ficheiros do pedido é de melhor esforço e vem depois de entrar: uma falha aí não deita fora a conta
    // que o outro computador acabou de entregar.
    [Fact]
    public async Task CleanUpFailure_AfterTheSecretArrived_DoesNotLoseTheAccount()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        novo.Transport.Fail("delete", "", times: 100, Folder);
        novo.Transport.Fail("list", "pair/" + session + "/", times: 100, Folder);
        await FinishAsync(casa, novo, session, code);
        Assert.Contains("foi aprovado", casa.Agent.Status.Notice);
    }

    // Ler o registo de chaves logo a seguir à entrega falha uma vez: a entrada repete-se, a conta não se perde.
    [Fact]
    public async Task JoinFailure_RightAfterTheSecretArrived_IsRetried()
    {
        var (casa, novo) = await TwoAsync();
        var (session, code) = await ReadyAsync(casa, novo);
        novo.Transport.Fail("read", "/keys", times: 2, Folder);
        await FinishAsync(casa, novo, session, code);
        Assert.Equal(2, novo.Transport.Thrown);
    }

    // Uma escrita que falha sempre (o limite do cliente HTTP, três vezes): o pedido acaba com um erro à vista — não fica
    // preso em «Falta um passo…» — e «Pedir outra vez» abre um pedido novo. A causa é o transporte: não se escreve a
    // recusa (o outro computador não conta um pedido mau).
    [Fact]
    public async Task WriteThatAlwaysTimesOut_EndsTheRequestWithAnError_AndAskingAgainWorks()
    {
        var (casa, novo) = await TwoAsync();
        novo.Transport.Fail("write", "/4-guest", times: 100, HttpTimeout);
        novo.Agent.RequestPairing();
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 1, "o pedido no transporte");
        await casa.Agent.CheckRequestsAsync();
        await _cloud.WaitFor(() => novo.Agent.Status.Pairing?.End == GuestEnd.Failed, "o fim do pedido com erro");
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.Equal(SyncAgent.Message(new HttpRequestException()), novo.Agent.Status.Pairing!.Error);
        Assert.Equal(3, novo.Transport.Thrown);
        Assert.Equal(0, _cloud.PairingFiles(PairingProtocol.Refused));

        novo.Transport.Clear();
        novo.Agent.RequestPairing();
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 2, "o pedido novo");
        Assert.Equal(SyncPhase.Pairing, novo.Agent.Status.Phase);
        Assert.Null(novo.Agent.Status.Pairing!.Error);
    }

    // Uma exceção que ninguém esperava no computador novo: o pedido acaba com um texto fixo (nunca a mensagem crua) e a
    // recusa segue para o outro lado.
    [Fact]
    public async Task UnexpectedFailure_OnTheGuest_EndsTheRequestWithAFixedText()
    {
        var (casa, novo) = await TwoAsync();
        novo.Transport.Fail("read", "/3-host", times: 1, () => new InvalidOperationException("pormenor interno"));
        novo.Agent.RequestPairing();
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 1, "o pedido no transporte");
        await casa.Agent.CheckRequestsAsync();
        await _cloud.WaitFor(() => novo.Agent.Status.Pairing?.End == GuestEnd.Failed, "o fim do pedido");
        Assert.Equal(SyncPhase.Choose, novo.Agent.Status.Phase);
        Assert.DoesNotContain("pormenor interno", novo.Agent.Status.Pairing!.Error);
        Assert.Contains("Peça outra vez", novo.Agent.Status.Pairing.Error);
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Refused) == 1, "a recusa escrita");
    }

    // Do lado de quem aprova: uma escrita que falha sempre, ou uma exceção inesperada, tira o pedido sem o deixar
    // «a responder» para sempre, e a sessão fica como acabada. A falha do transporte não conta para os pedidos em série.
    [Fact]
    public async Task HostFlow_ThatCannotGoOn_IsRemoved_AndMarkedAsEnded()
    {
        var (casa, novo) = await TwoAsync();
        casa.Transport.Fail("write", "/3-host", times: 100, Folder);
        novo.Agent.RequestPairing();
        await _cloud.WaitFor(() => _cloud.PairingFiles(PairingProtocol.Request) == 1, "o pedido no transporte");
        await casa.Agent.CheckRequestsAsync();
        await _cloud.WaitFor(() => casa.Transport.Thrown == 3 && casa.Agent.Status.AnsweringSinceMs == 0, "o fim do pedido na Casa");
        Assert.Empty(casa.Agent.Status.Requests);
        Assert.Equal(1, Ended(casa));
        Assert.Equal(0, BadCount(casa));
        novo.Agent.CancelPairing();

        // Uma exceção inesperada (não é do transporte): sai também, e conta.
        casa.Transport.Clear();
        var session = await PairingRequests.OpenAsync(_cloud, Now);
        casa.Transport.Fail("read", session + "/2-guest", times: 1, () => new InvalidOperationException("pormenor interno"));
        await casa.Agent.CheckRequestsAsync();
        await _cloud.WaitFor(() => casa.Transport.Thrown == 1 && casa.Agent.Status.AnsweringSinceMs == 0 && BadCount(casa) == 1, "o fim do 2.º pedido");
        Assert.True(Ended(casa) >= 2);
    }

    private static int Ended(Device device)
    {
        var ended = typeof(SyncAgent).GetField("_ended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(device.Agent)!;
        return (int)ended.GetType().GetProperty("Count")!.GetValue(ended)!;
    }

    // ---- o que conta como passageiro -------------------------------------------------------------------------------

    // O limite de tempo do cliente HTTP da sincronização: a exceção traz um token JÁ cancelado e uma TimeoutException
    // dentro. Conta como falha passageira (a ronda fica com o erro e o agendador tenta outra vez).
    [Fact]
    public async Task HttpClientTimeout_IsATransientFailure()
    {
        using var client = GoogleEndpoints.CreateClient(new NeverAnswers());
        client.Timeout = TimeSpan.FromMilliseconds(50);
        var thrown = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync(GoogleEndpoints.Files));
        Assert.IsType<TimeoutException>(thrown.InnerException);
        Assert.True(thrown.CancellationToken.IsCancellationRequested);
        Assert.True(SyncAgent.IsTransient(thrown));
        Assert.True(SyncAgent.IsTransient(HttpTimeout()));
        Assert.Equal(SyncAgent.Message(new HttpRequestException()), SyncAgent.Message(thrown));
        // Um pedido parado de propósito (o token de quem chamou) continua a não ser uma falha.
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.False(SyncAgent.IsTransient(new TaskCanceledException("parado", null, stopped.Token)));
    }

    private sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    // ---- ficheiros selados que não conferem --------------------------------------------------------------------------

    // Um ficheiro selado com a chave certa mas com menos de 4 bytes de conteúdo (nem o comprimento cabe), ou com um
    // conteúdo que não é Brotli: FormatException (o que quem lê já trata), nunca uma exceção de argumento.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void SealedFile_WithLessThanFourBytes_IsAFormatError(int length)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var file = SealRaw(key, SealedFile.Pairing, "pair", "contexto", new byte[length], brotli: true);
        Assert.Throws<FormatException>(() => SealedFile.Open(key, SealedFile.Pairing, "pair", "contexto", file));
    }

    [Fact]
    public void SealedFile_WithContentThatIsNotBrotli_IsAFormatError()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = new byte[4 + 64];
        BinaryPrimitives.WriteInt32BigEndian(plain, 64);
        Array.Fill(plain, (byte)0xFF, 4, 64);
        var file = SealRaw(key, SealedFile.Pairing, "pair", "contexto", plain, brotli: true);
        Assert.Throws<FormatException>(() => SealedFile.Open(key, SealedFile.Pairing, "pair", "contexto", file));
        // O mesmo conteúdo sem a opção de compressão lê-se tal e qual.
        var raw = SealRaw(key, SealedFile.Pairing, "pair", "contexto", plain, brotli: false);
        Assert.Equal(64, SealedFile.Open(key, SealedFile.Pairing, "pair", "contexto", raw).Length);
    }

    // Um ficheiro selado feito à mão, com o conteúdo cifrado tal e qual (sem o comprimento nem o preenchimento que
    // SealedFile.Seal põe sempre).
    private static byte[] SealRaw(byte[] key, char kind, string chain, string context, byte[] plain, bool brotli)
    {
        var header = new byte[11];
        "ETS"u8.CopyTo(header);
        header[3] = (byte)kind;
        header[4] = SealedFile.FormatVersion;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), 1);
        header[9] = brotli ? (byte)1 : (byte)0;
        var aad = header.Concat(Encoding.UTF8.GetBytes("|" + chain + "|" + context)).ToArray();
        var output = new byte[header.Length + 12 + plain.Length + 16];
        header.CopyTo(output, 0);
        var nonce = output.AsSpan(header.Length, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, output.AsSpan(header.Length + 12, plain.Length), output.AsSpan(output.Length - 16), aad);
        return output;
    }

    // Quem faz de anfitrião com o transporte na mão entrega um «segredo» com o tamanho errado, ou um selado sem
    // conteúdo: o computador novo recusa com a mensagem de sempre, em vez de uma exceção que ninguém trata.
    [Theory]
    [InlineData("tamanho")]
    [InlineData("vazio")]
    [InlineData("lixo")]
    public async Task Guest_GetsASecretThatDoesNotCheck_RefusesWithAPairingError(string kind)
    {
        var cloud = new MemoryTransport();
        var session = PairingProtocol.NewSession();
        using var hostKey = PairingProtocol.NewKey();
        var hostPublic = PairingProtocol.PublicKey(hostKey);
        var hostNonce = RandomNumberGenerator.GetBytes(32);
        var path = SyncPaths.Pairing(session);
        await cloud.WriteAsync(path + PairingProtocol.HostKey, Json.Bytes(new PairingMessage { Key = Kdf.Base64Url(hostPublic), CreatedMs = _now }));
        using var guest = await PairingGuest.JoinAsync(cloud, PairingProtocol.ShortCode(session), "Portátil", Now, Quick);
        var guestPublic = Kdf.FromBase64Url(Json.Parse<PairingMessage>((await cloud.ReadAsync(path + PairingProtocol.GuestKey))!).Key!);
        await cloud.WriteAsync(path + PairingProtocol.HostNonce, Json.Bytes(new PairingMessage { Nonce = Kdf.Base64Url(hostNonce), CreatedMs = _now }));
        await guest.WaitForCodeAsync();
        var guestNonce = Kdf.FromBase64Url(Json.Parse<PairingMessage>((await cloud.ReadAsync(path + PairingProtocol.GuestNonce))!).Nonce!);
        var transcript = PairingProtocol.Transcript(session, hostPublic, guestPublic, hostNonce, guestNonce);
        var key = PairingProtocol.SessionKey(hostKey, guestPublic, session, transcript);
        var context = PairingProtocol.SecretContext(session, transcript);
        var file = kind switch
        {
            "tamanho" => SealedFile.Seal(key, SealedFile.Pairing, "pair", 1, context,
                Json.Bytes(new PairingSecret { Secret = Kdf.Base64Url(new byte[5]), HostName = "Falso" })),
            "vazio" => SealRaw(key, SealedFile.Pairing, "pair", context, [], brotli: true),
            _ => SealedFile.Seal(key, SealedFile.Pairing, "pair", 1, context, "{ não é json"u8),
        };
        await cloud.WriteAsync(path + PairingProtocol.Sealed, file);
        guest.ConfirmCode();
        var refused = await Assert.ThrowsAsync<PairingException>(() => guest.WaitForSecretAsync());
        Assert.Contains("não confere", refused.Message);
    }

    // ---- o transporte que falha quando se lhe pede -------------------------------------------------------------------

    // Falha as próximas «times» operações do tipo dado cujo caminho acaba em «end» (lista: cujo prefixo acaba em «end»).
    // Cada falha avisa as esperas do teste.
    private sealed class FaultyTransport(ISyncTransport inner, Action pulse) : ISyncTransport
    {
        private sealed class Fault(string op, string end, int left, Func<Exception> make)
        {
            public string Op { get; } = op;
            public string End { get; } = end;
            public int Left { get; set; } = left;
            public Func<Exception> Make { get; } = make;
        }

        private readonly Lock _gate = new();
        private readonly List<Fault> _faults = [];
        private int _thrown;

        public int Thrown => Volatile.Read(ref _thrown);

        public string Name => inner.Name;

        public void Fail(string op, string end, int times, Func<Exception> make)
        {
            lock (_gate) _faults.Add(new Fault(op, end, times, make));
        }

        public void Clear()
        {
            lock (_gate) _faults.Clear();
            Volatile.Write(ref _thrown, 0);
        }

        public int Left(string op, string end)
        {
            lock (_gate) return _faults.Where(f => f.Op == op && f.End == end).Sum(f => f.Left);
        }

        private void Check(string op, string path)
        {
            Exception? failure = null;
            lock (_gate)
            {
                if (_faults.FirstOrDefault(f => f.Left > 0 && f.Op == op && path.EndsWith(f.End, StringComparison.Ordinal)) is { } fault)
                {
                    fault.Left--;
                    failure = fault.Make();
                }
            }
            if (failure is null) return;
            Interlocked.Increment(ref _thrown);
            pulse();
            throw failure;
        }

        public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
        {
            Check("list", prefix);
            return await inner.ListAsync(prefix, cancellationToken);
        }

        public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Check("read", path);
            return await inner.ReadAsync(path, cancellationToken);
        }

        public async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
        {
            Check("write", path);
            await inner.WriteAsync(path, data, cancellationToken);
        }

        public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            Check("delete", path);
            await inner.DeleteAsync(path, cancellationToken);
        }
    }
}
