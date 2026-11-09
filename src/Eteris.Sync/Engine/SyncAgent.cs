using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Sync.Engine;

// O que um aparelho guarda da sincronização (o anfitrião protege-o: DPAPI no Windows). Nunca vai para o transporte.
public sealed class SavedSync
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    // Segredo da conta (base64url). null = sem conta neste aparelho (nunca entrou, ou «Sair» com «apagar a chave»).
    [JsonPropertyName("secret")] public string? Secret { get; set; }
    [JsonPropertyName("state")] public SyncState? Engine { get; set; }
    // Como o perfil local estava no fim da última sincronização (ProfileSync). null = ainda não juntou.
    [JsonPropertyName("base")] public Dictionary<string, string>? Baseline { get; set; }
    // A sincronização está ligada neste aparelho (desliga-se com «Sair», ao apagar os dados ou se a conta desaparecer).
    [JsonPropertyName("on")] public bool Enabled { get; set; }
    // O código de recuperação ainda não foi confirmado como guardado («Já guardei»): é o único momento em que aparece.
    [JsonPropertyName("codePending")] public bool CodePending { get; set; }
    [JsonPropertyName("last")] public long LastSuccessMs { get; set; }
    [JsonPropertyName("compactMs")] public long LastCompactMs { get; set; }
    [JsonPropertyName("compactSeq")] public long LastCompactSeq { get; set; }
    // Porque parou (conta apagada, aparelho removido): mostrado até se ligar outra vez.
    [JsonPropertyName("stopped")] public string? Stopped { get; set; }
}

public interface ISyncStore
{
    SavedSync? Load();
    void Save(SavedSync saved);
    void Delete();
}

public enum SyncPhase
{
    Off,       // desligada neste aparelho
    Starting,  // a ver se a conta já existe
    Choose,    // a conta já existe: entrar por aprovação noutro aparelho ou pelo código de recuperação
    Pairing,   // à espera da aprovação noutro aparelho
    On,        // ligada
    Stopped,   // parou: a conta foi apagada ou este aparelho foi removido (diz porquê)
}

// Um pedido de um aparelho novo, já com o código de verificação, à espera de «Aprovar» neste.
public sealed record HostRequest(string Session, string Name, string Code, long SeenMs);

// Este aparelho a entrar por aprovação noutro: o código a mostrar (quando há), e o resultado.
public sealed record GuestPairing(string? Code, bool Confirmed, string? Error);

public sealed record DeviceInfo(string Id, string Name, string Kind, long LastSyncMs, bool IsThis);

public sealed record SyncStatus(SyncPhase Phase, bool Running, long LastSuccessMs, string? Error, string? Notice, string? RecoveryCode,
    GuestPairing? Pairing, IReadOnlyList<HostRequest> Requests, IReadOnlyList<DeviceInfo> Devices, bool NeedsUpdate, int PendingDevices);

// O aparelho na conta de sincronização: decide se cria a conta, entra (código ou aprovação), sincroniza, emparelha,
// remove aparelhos e apaga os dados. Uma operação de cada vez. Construir e ler o estado nunca faz pedidos: a rede só
// é usada quando o anfitrião chama RunAsync (o agendador), StartAsync (o utilizador entrou ou ligou) ou outra ação.
public sealed class SyncAgent
{
    private readonly Func<ISyncTransport> _transportFactory;
    private readonly ILocalProfile _profile;
    private readonly ISyncStore _store;
    private readonly Func<long> _now;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostFlow> _hosts = new(StringComparer.Ordinal);
    private ISyncTransport? _transport;
    private SavedSync? _saved;
    private SyncEngine? _engine;
    private SyncPhase _phase;
    private bool _running;
    private string? _error;
    private string? _notice;
    private bool _needsUpdate;
    private int _pendingDevices;
    private GuestFlow? _guest;
    private IReadOnlyList<DeviceInfo> _devices = [];
    // Já se leram os estados dos aparelhos (e fixaram as chaves) com o motor atual: uma vez por sessão e conta, e de
    // novo quando aparece um aparelho desconhecido. Um aparelho que ainda não enviou nada só se conhece por aqui.
    private SyncEngine? _pinnedFor;

    public SyncAgent(Func<ISyncTransport> transport, ILocalProfile profile, ISyncStore store, string deviceName, Func<long> now,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _transportFactory = transport;
        _profile = profile;
        _store = store;
        _now = now;
        _delay = delay;
        DeviceName = deviceName;
        _saved = store.Load();
        _phase = _saved is { Enabled: true } ? SyncPhase.On : _saved?.Stopped is not null ? SyncPhase.Stopped : SyncPhase.Off;
        _notice = _saved?.Stopped;
    }

    public string DeviceName { get; }

    // Intervalo entre perguntas ao transporte enquanto se espera pelo outro aparelho no emparelhamento.
    public TimeSpan PairingPoll { get; init; } = TimeSpan.FromSeconds(3);

    // Mudou alguma coisa no estado (para a interface se redesenhar). Chamado de qualquer thread.
    public event Action? Changed;

    public bool Enabled
    {
        get { lock (_gate) return _saved?.Enabled == true; }
    }

    // Há uma conta guardada neste aparelho (para retomar sem código depois de «Sair» sem apagar a chave).
    public bool HasKey
    {
        get { lock (_gate) return _saved?.Secret is not null; }
    }

    public SyncStatus Status
    {
        get
        {
            lock (_gate)
            {
                string? code = null;
                if (_saved is { CodePending: true, Secret: { } secret })
                {
                    using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
                    code = account.ToCode();
                }
                var requests = _hosts.Values.Where(h => h.Code is not null && _now() - h.Created < (long)PairingProtocol.Lifetime.TotalMilliseconds)
                    .Select(h => new HostRequest(h.Session, h.Name ?? "Aparelho sem nome", h.Code!, h.Created)).ToList();
                var pairing = _guest is null ? null : new GuestPairing(_guest.Code, _guest.Confirm.Task.IsCompleted, _guest.Error);
                return new SyncStatus(_phase, _running, _saved?.LastSuccessMs ?? 0, _error, _notice, code, pairing, requests, _devices, _needsUpdate, _pendingDevices);
            }
        }
    }

    // ---- ligar ------------------------------------------------------------------------------------------------------

    // O utilizador entrou (ou carregou em «Ligar»): liga a sincronização neste aparelho e faz a primeira ronda — retoma
    // a conta guardada, cria a conta se o transporte não tiver nenhuma, ou pede para escolher como entrar.
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _saved ??= new SavedSync();
            _saved.Enabled = true;
            _saved.Stopped = null;
            _notice = null;
            _error = null;
            _phase = SyncPhase.Starting;
            _store.Save(_saved);
        }
        Notify();
        await RunAsync(force: true, cancellationToken).ConfigureAwait(false);
    }

    // Uma ronda: o que fizer falta (retomar, criar, sincronizar). Devolve false se falhou (o agendador espera mais).
    public async Task<bool> RunAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return true;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        try
        {
            if (_engine is null)
            {
                SavedSync saved;
                lock (_gate) saved = _saved!;
                if (saved is { Secret: { } secret, Engine: { } state })
                {
                    if (!await ResumeAsync(secret, state, cancellationToken).ConfigureAwait(false)) return true;
                }
                else
                {
                    if (!force && _phase is SyncPhase.Choose or SyncPhase.Pairing) return true;
                    await SetUpAsync(cancellationToken).ConfigureAwait(false);
                    if (_engine is null) return true;
                }
            }
            await CycleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChainGoneException)
        {
            await ChainGoneAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (IsTransient(e))
        {
            Fail(e);
            return false;
        }
        finally
        {
            SetRunning(false);
            _run.Release();
        }
    }

    // Há alterações locais por enviar? (Sem rede: o agendador só sincroniza depois de uma alteração se houver.)
    public bool HasLocalChanges()
    {
        SavedSync? saved;
        lock (_gate) saved = _saved;
        if (saved is not { Enabled: true, Secret: not null }) return false;
        return ProfileSync.CountLocalChanges(_profile, saved.Baseline) > 0;
    }

    private async Task<bool> ResumeAsync(string secret, SyncState state, CancellationToken cancellationToken)
    {
        using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
        try
        {
            _engine = await SyncEngine.ResumeAsync(Transport(), account, state, _now, cancellationToken: cancellationToken).ConfigureAwait(false);
            SetPhase(SyncPhase.On);
            return true;
        }
        catch (WrongCodeException)
        {
            await StopAsync("A sincronização parou: a conta guardada neste computador não abre a do Google Drive.", cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private async Task SetUpAsync(CancellationToken cancellationToken)
    {
        var chains = await ChainsAsync(cancellationToken).ConfigureAwait(false);
        SavedSync saved;
        lock (_gate) saved = _saved!;
        // A chave ficou neste aparelho («Sair» sem a apagar) e a conta ainda existe: entra outra vez sem código.
        if (saved.Secret is { } kept)
        {
            using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(kept));
            if (chains.Contains(account.ChainId))
            {
                await JoinCoreAsync(account, null, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        if (chains.Count == 0)
        {
            using var account = AccountSecret.Create();
            var engine = await SyncEngine.CreateAsync(Transport(), account, DeviceIdentity.Create(DeviceName), _now, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _engine = engine;
                saved.Secret = Kdf.Base64Url(account.ToBytes());
                saved.Engine = engine.State;
                saved.Baseline = null;
                saved.CodePending = true;
                _phase = SyncPhase.On;
                _store.Save(saved);
            }
            return;
        }
        SetPhase(SyncPhase.Choose);
    }

    private async Task<HashSet<string>> ChainsAsync(CancellationToken cancellationToken)
    {
        var chains = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in await Transport().ListAsync(SyncPaths.Root, cancellationToken).ConfigureAwait(false))
            if (SyncPaths.TryParseKeys(path, out var chain)) chains.Add(chain);
        return chains;
    }

    // ---- sincronizar ------------------------------------------------------------------------------------------------

    private async Task CycleAsync(CancellationToken cancellationToken)
    {
        var engine = _engine!;
        SavedSync saved;
        lock (_gate) saved = _saved!;
        SyncReport report;
        Dictionary<string, string> baseline;
        try
        {
            (report, baseline) = await ProfileSync.RunAsync(engine, _profile, saved.Baseline, DeviceName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // O que ficou na caixa de saída (e o que já se leu) fica guardado mesmo que a ronda falhe a meio.
            lock (_gate)
            {
                saved.Engine = engine.State;
                _store.Save(saved);
            }
        }
        lock (_gate)
        {
            saved.Baseline = baseline;
            _store.Save(saved);
        }
        await engine.PushAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(_pinnedFor, engine) || engine.HasUnpinnedPeers)
        {
            await RefreshDevicesCoreAsync(cancellationToken).ConfigureAwait(false);
            _pinnedFor = engine;
        }
        var now = _now();
        if (now - saved.LastCompactMs > (long)TimeSpan.FromDays(1).TotalMilliseconds && engine.State.NextSeq - 1 > saved.LastCompactSeq)
        {
            await engine.CompactAsync(cancellationToken).ConfigureAwait(false);
            saved.LastCompactMs = now;
            saved.LastCompactSeq = engine.State.NextSeq - 1;
        }
        await AnswerRequestsCoreAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            saved.Engine = engine.State;
            saved.LastSuccessMs = now;
            _store.Save(saved);
            _error = null;
            _needsUpdate = report.NeedsUpdate;
            _pendingDevices = report.Pending.Count;
            _phase = SyncPhase.On;
        }
    }

    // A conta deste aparelho desapareceu: outro aparelho fez «nova cadeia» (e deixou-lhe a conta nova), removeu este
    // aparelho, ou os dados foram apagados.
    private async Task ChainGoneAsync(CancellationToken cancellationToken)
    {
        SavedSync saved;
        lock (_gate) saved = _saved!;
        var old = _engine?.State ?? saved.Engine;
        _engine = null;
        if (old is not null)
        {
            try
            {
                if (await SyncEngine.ReceiveRekeyAsync(Transport(), old, cancellationToken).ConfigureAwait(false) is { } rekey)
                {
                    using (rekey.Secret)
                    {
                        var engine = await SyncEngine.JoinAsync(Transport(), rekey.Secret, old.Device, _now, cancellationToken: cancellationToken).ConfigureAwait(false);
                        engine.AdoptDeviceKeys(old);
                        lock (_gate)
                        {
                            _engine = engine;
                            saved.Secret = Kdf.Base64Url(rekey.Secret.ToBytes());
                            saved.Engine = engine.State;
                            // Os dados deste computador não mudaram: a base continua a valer, e o que mudou desde a última
                            // ronda segue para a conta nova.
                            saved.LastCompactMs = 0;
                            saved.LastCompactSeq = 0;
                            _store.Save(saved);
                            _notice = rekey.FromName.Length > 0
                                ? $"A conta mudou de chave em «{rekey.FromName}» (um aparelho foi removido). Este computador continua ligado."
                                : "A conta mudou de chave (um aparelho foi removido). Este computador continua ligado.";
                        }
                    }
                    await CycleAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (ChainGoneException) { }
            catch (Exception e) when (IsTransient(e))
            {
                Fail(e);
                return;
            }
        }
        await StopAsync("A sincronização parou: os dados do Éteris foram apagados do Google Drive, ou este computador foi removido noutro aparelho.",
            cancellationToken).ConfigureAwait(false);
    }

    private Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _engine = null;
            if (_saved is not null)
            {
                _saved.Enabled = false;
                _saved.Secret = null;
                _saved.Engine = null;
                _saved.Baseline = null;
                _saved.CodePending = false;
                _saved.Stopped = reason;
                _store.Save(_saved);
            }
            _phase = SyncPhase.Stopped;
            _notice = reason;
            _devices = [];
        }
        return Task.CompletedTask;
    }

    // ---- entrar num aparelho novo -----------------------------------------------------------------------------------

    public async Task JoinWithCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        AccountSecret account;
        try { account = AccountSecret.FromCode(code); }
        catch (FormatException e) { throw new SyncUserException(e.Message); }
        using (account)
        {
            await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
            SetRunning(true);
            try
            {
                await JoinCoreAsync(account, null, cancellationToken).ConfigureAwait(false);
                await CycleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WrongCodeException or ChainGoneException)
            {
                throw new SyncUserException("Este código não abre a conta do Éteris neste Google Drive. Confirme o código (14 grupos de 4).");
            }
            catch (Exception e) when (IsTransient(e))
            {
                Fail(e);
                throw new SyncUserException(Message(e));
            }
            finally
            {
                SetRunning(false);
                _run.Release();
            }
        }
    }

    private async Task JoinCoreAsync(AccountSecret account, string? from, CancellationToken cancellationToken)
    {
        var engine = await SyncEngine.JoinAsync(Transport(), account, DeviceIdentity.Create(DeviceName), _now, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _engine = engine;
            _saved ??= new SavedSync();
            _saved.Enabled = true;
            _saved.Stopped = null;
            _saved.Secret = Kdf.Base64Url(account.ToBytes());
            _saved.Engine = engine.State;
            _saved.Baseline = null; // primeira vez: junta sem perder nada (ProfileSync)
            _saved.CodePending = false;
            _saved.LastCompactMs = 0;
            _saved.LastCompactSeq = 0;
            _store.Save(_saved);
            _phase = SyncPhase.On;
            _guest = null;
            _notice = from is null ? null : $"Este computador entrou na conta, aprovado em «{from}».";
        }
    }

    // «Aprovar noutro computador»: abre o pedido, espera que um aparelho ligado responda, mostra o código de
    // verificação e, depois de o utilizador confirmar aqui (ConfirmPairing) e lá, recebe a conta e junta os dados.
    public void RequestPairing()
    {
        GuestFlow flow;
        lock (_gate)
        {
            if (_guest is { Error: null }) return;
            _guest = flow = new GuestFlow();
            _phase = SyncPhase.Pairing;
            _error = null;
        }
        Notify();
        _ = Task.Run(async () =>
        {
            string? session = null;
            var transport = Transport();
            try
            {
                session = await PairingRequests.OpenAsync(transport, _now, flow.Cancel.Token).ConfigureAwait(false);
                flow.Session = session;
                using var guest = await PairingRequests.WaitForHostAsync(transport, session, DeviceName, _now, _delay, PairingPoll, flow.Cancel.Token)
                    .ConfigureAwait(false);
                var code = await guest.WaitForCodeAsync(flow.Cancel.Token).ConfigureAwait(false);
                lock (_gate) flow.Code = code;
                Notify();
                // À espera da confirmação do utilizador aqui; entretanto, o outro aparelho pode recusar.
                while (!flow.Confirm.Task.IsCompleted)
                {
                    if (await PairingRequests.IsRefusedAsync(transport, session, flow.Cancel.Token).ConfigureAwait(false))
                        throw new PairingException("O outro aparelho recusou o pedido.");
                    await Task.WhenAny(flow.Confirm.Task, (_delay ?? Task.Delay)(PairingPoll, flow.Cancel.Token)).ConfigureAwait(false);
                    flow.Cancel.Token.ThrowIfCancellationRequested();
                }
                guest.ConfirmCode();
                using var account = await guest.WaitForSecretAsync(flow.Cancel.Token).ConfigureAwait(false);
                await PairingRequests.CleanUpAsync(transport, session, CancellationToken.None).ConfigureAwait(false);
                session = null;
                await _run.WaitAsync(flow.Cancel.Token).ConfigureAwait(false);
                SetRunning(true);
                try
                {
                    await JoinCoreAsync(account, guest.HostName, flow.Cancel.Token).ConfigureAwait(false);
                    await CycleAsync(flow.Cancel.Token).ConfigureAwait(false);
                }
                finally
                {
                    SetRunning(false);
                    _run.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is PairingException or WrongCodeException or ChainGoneException || IsTransient(e))
            {
                lock (_gate)
                {
                    flow.Error = e is PairingException ? e.Message : Message(e);
                    if (_phase == SyncPhase.Pairing) _phase = SyncPhase.Choose;
                }
                if (session is not null)
                {
                    try { await PairingRequests.RefuseAsync(transport, session, _now, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception x) when (IsTransient(x)) { }
                }
            }
            Notify();
        });
    }

    // O utilizador confirmou NESTE aparelho que o código é igual ao do outro.
    public void ConfirmPairing()
    {
        lock (_gate)
        {
            if (_guest?.Code is null) return;
            _guest.Confirm.TrySetResult();
        }
        Notify();
    }

    public void CancelPairing()
    {
        GuestFlow? flow;
        lock (_gate)
        {
            flow = _guest;
            _guest = null;
            if (_phase == SyncPhase.Pairing) _phase = SyncPhase.Choose;
        }
        if (flow is null) return;
        flow.Cancel.Cancel();
        if (flow.Session is { } session)
        {
            var transport = Transport();
            // O outro aparelho deixa de esperar (vê a recusa); os ficheiros saem na limpeza dos pedidos abandonados.
            _ = Task.Run(async () =>
            {
                try { await PairingRequests.RefuseAsync(transport, session, _now).ConfigureAwait(false); }
                catch (Exception e) when (IsTransient(e)) { }
            });
        }
        Notify();
    }

    // ---- aprovar um aparelho novo -----------------------------------------------------------------------------------

    // Pedidos de aparelhos novos (no menu da conta e nas Definições, por ação do utilizador; e em cada ronda): responde
    // a cada um e, quando o aparelho novo revela o código de verificação, o pedido aparece em Status.Requests.
    public async Task CheckRequestsAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || _engine is null) return;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AnswerRequestsCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e)) { }
        finally { _run.Release(); }
    }

    private async Task AnswerRequestsCoreAsync(CancellationToken cancellationToken)
    {
        if (_engine is null) return;
        var transport = Transport();
        foreach (var session in await PairingRequests.PendingAsync(transport, _now, cancellationToken).ConfigureAwait(false))
        {
            lock (_gate)
                if (_hosts.ContainsKey(session) || _guest?.Session == session) continue;
            var host = await PairingHost.AnswerAsync(transport, session, _now, _delay, cancellationToken).ConfigureAwait(false);
            var flow = new HostFlow(session, host, _now());
            lock (_gate) _hosts[session] = flow;
            _ = Task.Run(async () =>
            {
                try
                {
                    var code = await host.WaitForGuestAsync(flow.Cancel.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        flow.Name = host.GuestName;
                        flow.Code = code;
                    }
                }
                catch (Exception e) when (e is PairingException or OperationCanceledException || IsTransient(e))
                {
                    lock (_gate) _hosts.Remove(session);
                    host.Dispose();
                }
                Notify();
            });
        }
        lock (_gate)
            foreach (var stale in _hosts.Values.Where(h => _now() - h.Created > (long)PairingProtocol.Lifetime.TotalMilliseconds).ToList())
            {
                _hosts.Remove(stale.Session);
                stale.Cancel.Cancel();
            }
    }

    // O utilizador confirmou NESTE aparelho que o aparelho novo mostra o mesmo código: entrega-lhe a conta, cifrada.
    public async Task ApproveAsync(string session, CancellationToken cancellationToken = default)
    {
        HostFlow? flow;
        string? secret;
        lock (_gate)
        {
            _hosts.TryGetValue(session, out flow);
            secret = _saved?.Secret;
        }
        if (flow?.Code is null || secret is null) throw new SyncUserException("Este pedido já não está à espera. Peça outra vez no outro computador.");
        using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
        try { await flow.Host.ApproveAsync(account, DeviceName, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is PairingException || IsTransient(e)) { throw new SyncUserException(e is PairingException ? e.Message : Message(e)); }
        lock (_gate)
        {
            _hosts.Remove(session);
            _notice = $"«{flow.Name}» foi aprovado: os dados juntam-se nesse computador.";
        }
        flow.Host.Dispose();
        Notify();
    }

    public async Task RefuseAsync(string session, CancellationToken cancellationToken = default)
    {
        HostFlow? flow;
        lock (_gate)
        {
            _hosts.Remove(session, out flow);
        }
        if (flow is null) return;
        flow.Cancel.Cancel();
        try { await flow.Host.RefuseAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e)) { }
        flow.Host.Dispose();
        Notify();
    }

    // ---- aparelhos, remover, apagar, sair ---------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeviceInfo>> RefreshDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_engine is null) return Status.Devices;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RefreshDevicesCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e)) { }
        finally { _run.Release(); }
        Notify();
        return Status.Devices;
    }

    private async Task RefreshDevicesCoreAsync(CancellationToken cancellationToken)
    {
        if (_engine is not { } engine) return;
        var states = await engine.DevicesAsync(cancellationToken).ConfigureAwait(false);
        var list = states
            .Select(s => new DeviceInfo(s.Device, s.Name.Length > 0 ? s.Name : "Aparelho sem nome", s.Kind, s.LastSyncMs, s.Device == engine.Device.Id))
            .OrderByDescending(d => d.IsThis).ThenByDescending(d => d.LastSyncMs).ToList();
        if (!list.Any(d => d.IsThis)) list.Insert(0, new DeviceInfo(engine.Device.Id, engine.Device.Name, engine.Device.Kind, engine.State.LastPullMs, true));
        lock (_gate)
        {
            _devices = list;
            if (_saved is not null)
            {
                _saved.Engine = engine.State; // chaves fixadas
                _store.Save(_saved);
            }
        }
    }

    // Remove um aparelho da conta: nova cadeia (código de recuperação novo, mostrado uma vez); os aparelhos que ficam
    // recebem a conta nova sozinhos, o removido deixa de conseguir ler e escrever.
    public async Task RemoveDeviceAsync(string device, CancellationToken cancellationToken = default)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        try
        {
            if (_engine is not { } engine) throw new SyncUserException("A sincronização não está ligada neste computador.");
            if (device == engine.Device.Id) throw new SyncUserException("Para tirar este computador da conta, use «Sair».");
            var name = _devices.FirstOrDefault(d => d.Id == device)?.Name ?? "o aparelho";
            var (rotated, secret) = await engine.RotateChainAsync(device, cancellationToken).ConfigureAwait(false);
            using (secret)
            {
                lock (_gate)
                {
                    _engine = rotated;
                    _saved!.Secret = Kdf.Base64Url(secret.ToBytes());
                    _saved.Engine = rotated.State;
                    _saved.CodePending = true;
                    _saved.LastCompactMs = _now();
                    _saved.LastCompactSeq = rotated.State.NextSeq - 1;
                    _store.Save(_saved);
                    _notice = $"«{name}» foi removido. A conta tem um código de recuperação novo (o antigo deixou de servir); os outros aparelhos continuam ligados sozinhos.";
                }
            }
            await rotated.PullAsync(cancellationToken).ConfigureAwait(false);
            await RefreshDevicesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsTransient(e))
        {
            Fail(e);
            throw new SyncUserException(Message(e));
        }
        finally
        {
            SetRunning(false);
            _run.Release();
        }
        Notify();
    }

    // «Apagar os meus dados do Éteris»: apaga todos os ficheiros do Éteris no transporte (todas as contas, pedidos de
    // emparelhamento). Não sai da conta Google. A sincronização fica desligada neste aparelho; nos outros para.
    public async Task DeleteCloudDataAsync(CancellationToken cancellationToken = default)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        try
        {
            var transport = Transport();
            foreach (var path in await transport.ListAsync(SyncPaths.Root, cancellationToken).ConfigureAwait(false))
                await transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            lock (_gate) _hosts.Clear();
            await StopAsync("Os dados do Éteris foram apagados do Google Drive. A sincronização está desligada; os dados deste computador ficaram.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (IsTransient(e))
        {
            Fail(e);
            throw new SyncUserException(Message(e));
        }
        finally
        {
            SetRunning(false);
            _run.Release();
        }
        Notify();
    }

    // «Sair»: a sincronização para neste aparelho. Com forgetKey, a cópia local da conta sai também (para voltar a
    // entrar é preciso o código ou outro aparelho); sem ela, entrar outra vez retoma sem código.
    public void SignOut(bool forgetKey)
    {
        GuestFlow? guest;
        List<HostFlow> hosts;
        lock (_gate)
        {
            guest = _guest;
            _guest = null;
            hosts = _hosts.Values.ToList();
            _hosts.Clear();
            _engine = null;
            _transport = null;
            _devices = [];
            _error = null;
            _notice = null;
            _phase = SyncPhase.Off;
            if (forgetKey)
            {
                _saved = null;
                _store.Delete();
            }
            else if (_saved is not null)
            {
                _saved.Enabled = false;
                _saved.CodePending = false;
                _store.Save(_saved);
            }
        }
        guest?.Cancel.Cancel();
        foreach (var host in hosts) host.Cancel.Cancel();
        Notify();
    }

    public void AcknowledgeCode()
    {
        lock (_gate)
        {
            if (_saved is null) return;
            _saved.CodePending = false;
            _store.Save(_saved);
        }
        Notify();
    }

    // ---- auxiliares -------------------------------------------------------------------------------------------------

    private ISyncTransport Transport()
    {
        lock (_gate) return _transport ??= _transportFactory();
    }

    private void SetPhase(SyncPhase phase)
    {
        lock (_gate) _phase = phase;
        Notify();
    }

    private void SetRunning(bool running)
    {
        lock (_gate) _running = running;
        Notify();
    }

    private void Fail(Exception e)
    {
        lock (_gate)
        {
            _error = Message(e);
            if (_phase == SyncPhase.Starting) _phase = SyncPhase.On;
        }
    }

    private void Notify() => Changed?.Invoke();

    // Falhas que passam sozinhas (rede, sessão, pasta): ficam no estado e o agendador tenta outra vez mais tarde.
    public static bool IsTransient(Exception e)
        => e is HttpRequestException or IOException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or TimeoutException
            or UnauthorizedAccessException or CryptographicException or FormatException or Google.OAuthException or UnsupportedFormatException;

    public static string Message(Exception e) => e switch
    {
        Google.OAuthException o => o.Message,
        HttpRequestException or TaskCanceledException or TimeoutException => "Sem ligação ao Google Drive. O Éteris tenta outra vez sozinho.",
        IOException or UnauthorizedAccessException => "A pasta de sincronização não está acessível. O Éteris tenta outra vez sozinho.",
        WrongCodeException w => w.Message,
        UnsupportedFormatException u => u.Message,
        _ => "Um ficheiro da sincronização não se leu (" + e.Message + "). O Éteris tenta outra vez mais tarde.",
    };

    private sealed class GuestFlow
    {
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource Confirm { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Session { get; set; }
        public string? Code { get; set; }
        public string? Error { get; set; }
    }

    private sealed class HostFlow(string session, PairingHost host, long created)
    {
        public string Session { get; } = session;
        public PairingHost Host { get; } = host;
        public long Created { get; } = created;
        public CancellationTokenSource Cancel { get; } = new();
        public string? Name { get; set; }
        public string? Code { get; set; }
    }
}

// Uma ação do utilizador não se pôde cumprir: a mensagem é para lhe mostrar.
public sealed class SyncUserException(string message) : Exception(message);
