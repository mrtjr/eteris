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
    // Troca de chave a meio (a conta nova já está aqui; falta entregá-la aos outros e apagar a antiga).
    [JsonPropertyName("rotation")] public RotationPending? Rotation { get; set; }
    // Com que conta do transporte (a conta Google, como impressão digital) este aparelho sincroniza: com outra sessão,
    // a sincronização fica em pausa sem apagar nada (SyncAgent.AccountKey).
    [JsonPropertyName("account")] public string? Account { get; set; }
    // Quando este aparelho criou a conta (0 = não a criou, ou já passou o tempo de ver se outro a criou ao mesmo tempo).
    [JsonPropertyName("created")] public long CreatedMs { get; set; }
    // As coleções ligadas NESTE aparelho (tópico «O que passa entre PCs»; null = as de omissão, SyncGroups). Desligar uma
    // linha para de enviar e de aplicar essas coleções e não apaga nada, nem aqui nem na conta.
    [JsonPropertyName("collections")] public List<string>? Collections { get; set; }
    // As coleções que já fizeram a «primeira junção» com a conta (null = estado de antes destas linhas: com base, as
    // coleções que já existiam). Uma coleção ligada outra vez sai daqui e junta-se de novo sem perder nada.
    [JsonPropertyName("joined")] public List<string>? Joined { get; set; }
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
    GuestPairing? Pairing, IReadOnlyList<HostRequest> Requests, IReadOnlyList<DeviceInfo> Devices, bool NeedsUpdate, int PendingDevices)
{
    // A autorização da Google expirou ou foi retirada: nada se resolve sozinho, é preciso entrar outra vez.
    public bool NeedsSignIn { get; init; }

    // Pedidos de aparelhos novos vistos numa ronda e ainda sem resposta: só se responde quando o utilizador abre o menu
    // ou a secção Sincronização (CheckRequestsAsync).
    public int WaitingRequests { get; init; }
}

// O aparelho na conta de sincronização: decide se cria a conta, entra (código ou aprovação), sincroniza, emparelha,
// remove aparelhos e apaga os dados. Uma operação de cada vez. Construir e ler o estado nunca faz pedidos: a rede só
// é usada quando o anfitrião chama RunAsync (o agendador), StartAsync (o utilizador entrou ou ligou) ou outra ação.
//
// «Sair» não espera pela ronda em curso: cada gravação confere primeiro que o estado ainda é o mesmo objeto e que a
// sincronização continua ligada (IsCurrent). Assim uma ronda que acaba depois de «Sair» (com «apagar a chave») não
// volta a gravar a chave que o utilizador pediu para apagar.
public sealed class SyncAgent
{
    // Saltos seguidos por contas que também já desapareceram (duas remoções enquanto este aparelho estava desligado).
    private const int MaxRekeyHops = 8;

    // Depois de criar a conta, durante quanto tempo se confere se outro computador criou outra ao mesmo tempo.
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);

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
    private bool _needsSignIn;
    private int _pendingDevices;
    private int _waiting;
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

    // A conta do transporte com que se está agora (para o Google Drive, uma impressão digital da conta Google; null =
    // não se sabe, ou o transporte não tem contas). Só disco: nunca faz pedidos.
    public Func<string?>? AccountKey { get; init; }

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

    // As coleções ligadas neste aparelho (as de omissão até o utilizador escolher). Só disco.
    public IReadOnlySet<string> EnabledCollections
    {
        get
        {
            lock (_gate) return EnabledOf(_saved);
        }
    }

    private static IReadOnlySet<string> EnabledOf(SavedSync? saved)
        => saved?.Collections is { } chosen ? chosen.ToHashSet(StringComparer.Ordinal) : Model.SyncGroups.DefaultCollections;

    // Liga ou desliga coleções neste aparelho. Desligar não apaga nada; ligar outra vez faz a «primeira junção» só delas
    // (o que só existe aqui sobe; o que a conta tem vale). A confirmação do Windows para Pagamentos e Documentos é do
    // anfitrião, antes de chamar isto.
    public void SetCollections(IEnumerable<string> collections, bool on)
    {
        lock (_gate)
        {
            _saved ??= new SavedSync();
            var enabled = EnabledOf(_saved).ToHashSet(StringComparer.Ordinal);
            var joined = JoinedOf(_saved).ToHashSet(StringComparer.Ordinal);
            foreach (var collection in collections)
            {
                if (on) enabled.Add(collection);
                else enabled.Remove(collection);
                joined.Remove(collection);
            }
            _saved.Collections = enabled.Order(StringComparer.Ordinal).ToList();
            _saved.Joined = joined.Order(StringComparer.Ordinal).ToList();
            _store.Save(_saved);
        }
        Notify();
    }

    // Há segredos em jogo neste aparelho (coleções secretas ligadas ou registos secretos na conta): aprovar um aparelho
    // novo entrega-lhe também as palavras-passe, os pagamentos e os documentos.
    public bool HasSecrets
    {
        get
        {
            lock (_gate)
                return EnabledOf(_saved).Any(Model.Collections.IsSecret)
                       || _engine?.State.Records.Values.Any(r => !r.Deleted && Model.Collections.IsSecret(r.Collection)) == true;
        }
    }

    // As já juntadas (um estado de antes das linhas: com base, as coleções que existiam antes das secretas).
    private static IReadOnlyCollection<string> JoinedOf(SavedSync saved)
        => saved.Joined ?? (saved.Baseline is null ? [] : [.. Model.SyncGroups.LegacyCollections]);

    private static ProfileSyncOptions OptionsOf(SavedSync saved)
    {
        var enabled = EnabledOf(saved);
        var joined = JoinedOf(saved);
        return new ProfileSyncOptions { Enabled = enabled, Fresh = enabled.Where(c => !joined.Contains(c)).ToHashSet(StringComparer.Ordinal) };
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
                return new SyncStatus(_phase, _running, _saved?.LastSuccessMs ?? 0, _error, _notice, code, pairing, requests, _devices, _needsUpdate, _pendingDevices)
                {
                    NeedsSignIn = _needsSignIn,
                    WaitingRequests = _waiting,
                };
            }
        }
    }

    // ---- ligar ------------------------------------------------------------------------------------------------------

    // O utilizador entrou (ou carregou em «Ligar»): liga a sincronização neste aparelho e faz a primeira ronda — retoma
    // a conta guardada, cria a conta se o transporte não tiver nenhuma, ou pede para escolher como entrar.
    // Se a sessão do transporte for de outra conta (outra conta Google), a sincronização fica em pausa sem apagar nada;
    // só com «switchAccount» (o utilizador carregou em «Ligar outra vez» depois de ler o aviso) passa para a conta nova.
    public async Task StartAsync(CancellationToken cancellationToken = default) => await StartAsync(switchAccount: false, cancellationToken).ConfigureAwait(false);

    public async Task StartAsync(bool switchAccount, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _saved ??= new SavedSync();
            _saved.Enabled = true;
            _saved.Stopped = null;
            if (switchAccount && OtherAccount(_saved))
            {
                // A conta guardada é de outra sessão: recomeça nesta (a chave antiga fica até haver uma conta nova).
                _engine = null;
                _saved.Engine = null;
                _saved.Baseline = null;
                _saved.Joined = null;
                _saved.Rotation = null;
                _saved.CreatedMs = 0;
                _saved.Account = AccountKey?.Invoke();
            }
            _notice = null;
            _error = null;
            _needsSignIn = false;
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
        SavedSync? saved;
        lock (_gate) saved = _saved;
        try
        {
            if (saved is not { Enabled: true }) return true;
            if (OtherAccount(saved))
            {
                Stop(saved, OtherAccountText, StopKeeps.Everything);
                return true;
            }
            if (_engine is null)
            {
                if (saved is { Secret: { } secret, Engine: { } state })
                {
                    if (!await ResumeAsync(saved, secret, state, cancellationToken).ConfigureAwait(false)) return true;
                }
                else
                {
                    if (!force && _phase is SyncPhase.Choose or SyncPhase.Pairing) return true;
                    await SetUpAsync(saved, cancellationToken).ConfigureAwait(false);
                    if (_engine is null) return true;
                }
            }
            if (saved.Rotation is { } rotation) await FinishRotationAsync(saved, rotation, cancellationToken).ConfigureAwait(false);
            await CycleAsync(saved, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChainGoneException)
        {
            await ChainGoneAsync(saved!, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (IsTransient(e))
        {
            Fail(e);
            return false;
        }
        finally
        {
            // «Sair» a meio: o motor desta ronda já não é o deste aparelho.
            lock (_gate)
                if (!IsCurrent(saved))
                    _engine = null;
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
        ProfileSyncOptions options;
        lock (_gate) options = OptionsOf(saved);
        return ProfileSync.CountLocalChanges(_profile, saved.Baseline, options) > 0;
    }

    private async Task<bool> ResumeAsync(SavedSync saved, string secret, SyncState state, CancellationToken cancellationToken)
    {
        using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
        try
        {
            var engine = await SyncEngine.ResumeAsync(Transport(), account, state, _now, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(saved)) return false;
                _engine = engine;
                _phase = SyncPhase.On;
            }
            Notify();
            return true;
        }
        catch (WrongCodeException)
        {
            // O registo de chaves no transporte não abre com a chave guardada (alterado ou danificado): para, sem apagar
            // nada deste computador — a chave fica, para quando o registo voltar ao normal ou para o utilizador decidir.
            Stop(saved, WrongKeysText, StopKeeps.Everything);
            return false;
        }
    }

    private async Task SetUpAsync(SavedSync saved, CancellationToken cancellationToken)
    {
        var chains = await ChainsAsync(cancellationToken).ConfigureAwait(false);
        // A chave ficou neste aparelho («Sair» sem a apagar, ou parou sem a apagar) e a conta ainda existe: entra outra
        // vez sem código.
        if (saved.Secret is { } kept)
        {
            using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(kept));
            if (chains.Contains(account.ChainId))
            {
                try
                {
                    await JoinCoreAsync(account, null, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (WrongCodeException) { } // o registo dessa conta não abre: escolher como entrar
            }
        }
        if (chains.Count == 0)
        {
            using var account = AccountSecret.Create();
            var engine = await SyncEngine.CreateAsync(Transport(), account, DeviceIdentity.Create(DeviceName), _now, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(saved)) return;
                _engine = engine;
                saved.Secret = Kdf.Base64Url(account.ToBytes());
                saved.Engine = engine.State;
                saved.Baseline = null;
                saved.Joined = null;
                saved.CodePending = true;
                saved.CreatedMs = _now();
                saved.Account = AccountKey?.Invoke();
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

    private async Task CycleAsync(SavedSync saved, CancellationToken cancellationToken)
    {
        var engine = _engine!;
        SyncReport report;
        Dictionary<string, string> baseline;
        ProfileSyncOptions options;
        lock (_gate) options = OptionsOf(saved);
        try
        {
            (report, baseline) = await ProfileSync.RunAsync(engine, _profile, saved.Baseline, DeviceName, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // O que ficou na caixa de saída (e o que já se leu) fica guardado mesmo que a ronda falhe a meio.
            lock (_gate)
                if (IsCurrent(saved) && ReferenceEquals(_engine, engine))
                {
                    saved.Engine = engine.State;
                    _store.Save(saved);
                }
        }
        lock (_gate)
        {
            if (!IsCurrent(saved) || !ReferenceEquals(_engine, engine)) return;
            // As coleções que fizeram a ronda inteira já estão juntas (as ligadas entretanto ficam por juntar).
            var joined = JoinedOf(saved).ToHashSet(StringComparer.Ordinal);
            var enabledNow = EnabledOf(saved);
            joined.UnionWith(report.Joined.Where(c => options.IsEnabled(c) && enabledNow.Contains(c)));
            saved.Baseline = baseline;
            saved.Joined = joined.Order(StringComparer.Ordinal).ToList();
            _store.Save(saved);
            if (_profile is ILocalNotices notices && notices.TakeNotice() is { } notice) _notice = notice;
        }
        if (await LeaveDuplicateAsync(saved, engine, cancellationToken).ConfigureAwait(false)) return;
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
        // Numa ronda, os pedidos de aparelhos novos só se contam (para o menu dizer que há um): responder-lhes faz
        // pedidos durante minutos e só acontece quando o utilizador abre o menu ou a secção Sincronização.
        await AnswerRequestsCoreAsync(answer: false, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!IsCurrent(saved) || !ReferenceEquals(_engine, engine)) return;
            saved.Engine = engine.State;
            saved.LastSuccessMs = now;
            if (saved.Account is null && AccountKey?.Invoke() is { } account) saved.Account = account;
            _store.Save(saved);
            _error = null;
            _needsSignIn = false;
            _needsUpdate = report.NeedsUpdate;
            _pendingDevices = report.Pending.Count;
            _phase = SyncPhase.On;
        }
    }

    // Dois computadores entraram pela primeira vez quase ao mesmo tempo, com o transporte vazio, e cada um criou a sua
    // conta. Enquanto este aparelho estiver sozinho na conta que criou (e só na primeira meia hora), confere: se houver
    // outra conta com o identificador menor, essa fica (o mesmo critério nos dois) e este aparelho sai da sua — apaga-a
    // do transporte e volta a «escolher como entrar», sem perder nada deste computador (a junção é sem perda).
    private async Task<bool> LeaveDuplicateAsync(SavedSync saved, SyncEngine engine, CancellationToken cancellationToken)
    {
        if (saved.CreatedMs == 0) return false;
        if (_now() - saved.CreatedMs > (long)DuplicateWindow.TotalMilliseconds)
        {
            lock (_gate)
                if (IsCurrent(saved))
                {
                    saved.CreatedMs = 0;
                    _store.Save(saved);
                }
            return false;
        }
        var files = await Transport().ListAsync(SyncPaths.Root, cancellationToken).ConfigureAwait(false);
        var mine = engine.ChainId;
        if (files.Any(f => SyncPaths.TryParseState(mine, f, out var device) && device != engine.Device.Id))
        {
            // Já há outro aparelho nesta conta: fica.
            lock (_gate)
                if (IsCurrent(saved))
                {
                    saved.CreatedMs = 0;
                    _store.Save(saved);
                }
            return false;
        }
        var other = files.Select(f => SyncPaths.TryParseKeys(f, out var chain) ? chain : null).OfType<string>()
            .Where(c => c != mine).Order(StringComparer.Ordinal).FirstOrDefault();
        if (other is null || string.CompareOrdinal(other, mine) > 0) return false;
        await engine.DeleteEverythingAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!IsCurrent(saved)) return true;
            _engine = null;
            _devices = [];
            saved.Secret = null;
            saved.Engine = null;
            saved.Baseline = null;
            saved.Joined = null;
            saved.CodePending = false;
            saved.CreatedMs = 0;
            saved.Rotation = null;
            _store.Save(saved);
            _phase = SyncPhase.Choose;
            _notice = "Outro computador criou a conta de sincronização ao mesmo tempo que este. Junte este computador a essa conta (aprovação no outro computador ou o código de recuperação dele): os dados deste computador juntam-se aos da conta, nada se perde.";
        }
        return true;
    }

    // A conta deste aparelho desapareceu: outro aparelho fez «nova cadeia» (e deixou-lhe a conta nova), removeu este
    // aparelho, ou os dados foram apagados. Segue as contas novas em sequência (duas remoções seguidas enquanto este
    // aparelho estava desligado) até uma que exista.
    private async Task ChainGoneAsync(SavedSync saved, CancellationToken cancellationToken)
    {
        SyncState? old;
        lock (_gate)
        {
            if (!IsCurrent(saved)) return;
            old = _engine?.State ?? saved.Engine;
            _engine = null;
        }
        if (old is not null)
        {
            try
            {
                var transport = Transport();
                var chain = old.ChainId;
                var consumed = new List<string>();
                for (var hop = 0; hop < MaxRekeyHops; hop++)
                {
                    using var rekey = await SyncEngine.ReceiveRekeyAsync(transport, old, chain, cancellationToken).ConfigureAwait(false);
                    if (rekey is null) break;
                    if (rekey.Conflict)
                    {
                        Stop(saved, ConflictText, StopKeeps.Secret);
                        return;
                    }
                    consumed.AddRange(rekey.Paths);
                    SyncEngine engine;
                    try
                    {
                        engine = await SyncEngine.JoinAsync(transport, rekey.Secret!, old.Device, _now, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (ChainGoneException)
                    {
                        // Esta conta nova também já foi substituída: a mensagem seguinte está dentro dela.
                        chain = rekey.Secret!.ChainId;
                        continue;
                    }
                    engine.AdoptDeviceKeys(old, rekey.Devices);
                    engine.RestageOwn(old);
                    lock (_gate)
                    {
                        if (!IsCurrent(saved)) return;
                        _engine = engine;
                        saved.Secret = Kdf.Base64Url(rekey.Secret!.ToBytes());
                        saved.Engine = engine.State;
                        saved.Rotation = null;
                        saved.CreatedMs = 0;
                        // Os dados deste computador não mudaram: a base continua a valer, e o que mudou desde a última
                        // ronda segue para a conta nova; o que este aparelho tinha escrito volta a seguir com a hora
                        // original (RestageOwn), para não ser desfeito pelo instantâneo de quem trocou a chave.
                        saved.LastCompactMs = 0;
                        saved.LastCompactSeq = 0;
                        _store.Save(saved);
                        _notice = rekey.FromName.Length > 0
                            ? $"A conta mudou de chave em «{rekey.FromName}» (um aparelho foi removido, ou há um código de recuperação novo). Este computador continua ligado."
                            : "A conta mudou de chave (um aparelho foi removido, ou há um código de recuperação novo). Este computador continua ligado.";
                    }
                    // Só agora, com a conta nova guardada, as mensagens saem do transporte (melhor esforço).
                    try { await SyncEngine.ForgetRekeysAsync(transport, consumed, cancellationToken).ConfigureAwait(false); }
                    catch (Exception e) when (IsTransient(e)) { }
                    await CycleAsync(saved, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (ChainGoneException) { }
            catch (Exception e) when (IsTransient(e))
            {
                // Sem rede a meio: tenta outra vez na próxima ronda (o estado antigo continua guardado).
                Fail(e);
                return;
            }
        }
        Stop(saved, GoneText, StopKeeps.Secret);
    }

    private enum StopKeeps
    {
        Nothing,    // os dados foram apagados por este aparelho: a chave já não serve para nada
        Secret,     // a conta desapareceu: guarda a chave (outra sessão Google, ou para entrar outra vez sem código)
        Everything, // pausa (outra conta, registo de chaves que não abre): nada deste computador muda
    }

    private void Stop(SavedSync saved, string reason, StopKeeps keeps)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_saved, saved)) return;
            _engine = null;
            saved.Enabled = false;
            if (keeps != StopKeeps.Everything)
            {
                saved.Engine = null;
                saved.Baseline = null;
                saved.Joined = null;
                saved.Rotation = null;
                saved.CreatedMs = 0;
            }
            if (keeps == StopKeeps.Nothing)
            {
                saved.Secret = null;
                saved.CodePending = false;
            }
            saved.Stopped = reason;
            _store.Save(saved);
            _phase = SyncPhase.Stopped;
            _notice = reason;
            _devices = [];
        }
        Notify();
    }

    private const string GoneText =
        "A sincronização parou: a conta de sincronização deste computador já não está neste Google Drive — os dados foram apagados, este computador foi removido noutro aparelho, ou a sessão é de outra conta Google. A chave ficou neste computador; nada daqui foi apagado.";

    private const string ConflictText =
        "A sincronização parou: dois aparelhos deixaram a este computador contas novas que não batem certo (um deles pode ter sido removido da conta). Para continuar, junte este computador outra vez à conta, com o código de recuperação novo ou por aprovação noutro computador.";

    private const string WrongKeysText =
        "A sincronização parou: o registo de chaves da conta no Google Drive não abre com a chave guardada neste computador (foi alterado ou danificou-se). A chave ficou neste computador; nada foi apagado.";

    private const string OtherAccountText =
        "A sincronização está em pausa: a sessão é de outra conta Google, diferente da que este computador usa para sincronizar. Nada foi apagado. Para continuar com a conta anterior, saia e entre com ela; para sincronizar com esta, carregue em «Ligar outra vez» (para voltar depois à anterior será preciso o código de recuperação dela ou outro computador).";

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
                SavedSync saved;
                lock (_gate) saved = _saved!;
                await CycleAsync(saved, cancellationToken).ConfigureAwait(false);
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
            _saved.Joined = null;
            _saved.CodePending = false;
            _saved.LastCompactMs = 0;
            _saved.LastCompactSeq = 0;
            _saved.Rotation = null;
            _saved.CreatedMs = 0;
            _saved.Account = AccountKey?.Invoke() ?? _saved.Account;
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
                    SavedSync saved;
                    lock (_gate) saved = _saved!;
                    await CycleAsync(saved, flow.Cancel.Token).ConfigureAwait(false);
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

    // Pedidos de aparelhos novos, por ação do utilizador (abriu o menu ou a secção Sincronização): responde a cada um e,
    // quando o aparelho novo revela o código de verificação, o pedido aparece em Status.Requests. Responder a um pedido
    // é acompanhá-lo (lê o Drive de 2 em 2 s até o aparelho novo responder, no máximo 10 minutos).
    public async Task CheckRequestsAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || _engine is null) return;
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AnswerRequestsCoreAsync(answer: true, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (IsTransient(e)) { }
        finally { _run.Release(); }
        Notify();
    }

    private async Task AnswerRequestsCoreAsync(bool answer, CancellationToken cancellationToken)
    {
        if (_engine is null) return;
        var transport = Transport();
        var waiting = 0;
        foreach (var session in await PairingRequests.PendingAsync(transport, _now, cancellationToken).ConfigureAwait(false))
        {
            lock (_gate)
                if (_hosts.ContainsKey(session) || _guest?.Session == session) continue;
            if (!answer)
            {
                waiting++;
                continue;
            }
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
        {
            _waiting = waiting;
            foreach (var stale in _hosts.Values.Where(h => _now() - h.Created > (long)PairingProtocol.Lifetime.TotalMilliseconds).ToList())
            {
                _hosts.Remove(stale.Session);
                stale.Cancel.Cancel();
            }
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
            // Um 2.º clique enquanto o 1.º ainda está na rede não volta a entregar a conta.
            if (flow is { Approving: true }) return;
            if (flow is { Code: not null } && secret is not null) flow.Approving = true;
        }
        if (flow?.Code is null || secret is null) throw new SyncUserException("Este pedido já não está à espera. Peça outra vez no outro computador.");
        using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
        try { await flow.Host.ApproveAsync(account, DeviceName, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is PairingException || IsTransient(e))
        {
            lock (_gate) flow.Approving = false;
            throw new SyncUserException(e is PairingException ? e.Message : Message(e));
        }
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
        // Depois de uma troca de chave, os aparelhos que ficaram ainda não escreveram o seu estado na conta nova: entram
        // na lista pela chave fixada (à espera da chave nova), para continuarem à vista e receberem uma troca seguinte.
        foreach (var peer in engine.State.PeerKeys.Keys)
            if (!list.Any(d => d.Id == peer))
                list.Add(new DeviceInfo(peer, engine.State.DeviceNames.TryGetValue(peer, out var name) && name.Length > 0 ? name : "Aparelho sem nome", "pc", 0, false));
        lock (_gate)
        {
            if (!ReferenceEquals(_engine, engine)) return;
            _devices = list;
            if (_saved is { Enabled: true })
            {
                _saved.Engine = engine.State; // chaves fixadas
                _store.Save(_saved);
            }
        }
    }

    // Remove um aparelho da conta: nova cadeia (código de recuperação novo, mostrado uma vez); os aparelhos que ficam
    // recebem a conta nova sozinhos, o removido deixa de conseguir ler e escrever.
    // «shown»: os aparelhos que o utilizador viu na lista quando decidiu (por omissão, a última lista). Só esses, e só
    // com a chave fixada antes desta ação, recebem a conta nova.
    public Task RemoveDeviceAsync(string device, IReadOnlyCollection<string>? shown = null, CancellationToken cancellationToken = default)
        => RotateAsync(device, shown, cancellationToken);

    // «Gerar um código de recuperação novo»: nova cadeia sem tirar ninguém; o código antigo deixa de servir.
    public Task NewRecoveryCodeAsync(CancellationToken cancellationToken = default) => RotateAsync(null, null, cancellationToken);

    private async Task RotateAsync(string? device, IReadOnlyCollection<string>? shown, CancellationToken cancellationToken)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        SavedSync? saved;
        lock (_gate) saved = _saved;
        try
        {
            if (_engine is null || saved is not { Enabled: true }) throw new SyncUserException("A sincronização não está ligada neste computador.");
            if (device == _engine.Device.Id) throw new SyncUserException("Para tirar este computador da conta, use «Sair».");
            if (saved.Rotation is { } unfinished) await FinishRotationAsync(saved, unfinished, cancellationToken).ConfigureAwait(false);
            var engine = _engine;
            // O que o utilizador viu, e as chaves fixadas ANTES desta ação: um aparelho que apareça agora não recebe nada.
            List<string> seen;
            string name;
            lock (_gate)
            {
                seen = (shown ?? _devices.Select(d => d.Id)).ToList();
                name = _devices.FirstOrDefault(d => d.Id == device)?.Name ?? "o aparelho";
            }
            var staying = engine.State.PeerKeys.Where(p => seen.Contains(p.Key) && p.Key != device && p.Key != engine.Device.Id)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

            // Primeiro uma ronda completa: o que os outros aparelhos enviaram e este ainda não leu entra no estado que
            // passa para a conta nova (a cadeia antiga, com esses lotes, vai ser apagada).
            await CycleAsync(saved, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(_engine, engine) || !IsCurrentLocked(saved)) throw new SyncUserException("A sincronização mudou entretanto. Tente outra vez.");
            var newcomers = engine.State.DeviceNames.Keys.Concat(engine.State.PeerKeys.Keys).Concat(engine.State.Read.Keys)
                .Where(d => d != engine.Device.Id && d != device && !seen.Contains(d)).Distinct(StringComparer.Ordinal)
                .Select(d => engine.State.DeviceNames.TryGetValue(d, out var n) && n.Length > 0 ? $"«{n}»" : "um aparelho sem nome").ToList();

            var (rotated, secret, pending) = await engine.StartRotationAsync(device, staying, cancellationToken).ConfigureAwait(false);
            using (secret)
            {
                lock (_gate)
                {
                    if (!IsCurrent(saved)) throw new SyncUserException("A sincronização foi desligada entretanto.");
                    // Guardado ANTES de mexer na conta antiga: uma falha a seguir continua na ronda seguinte.
                    _engine = rotated;
                    saved.Secret = Kdf.Base64Url(secret.ToBytes());
                    saved.Engine = rotated.State;
                    saved.Rotation = pending;
                    saved.CodePending = true;
                    saved.CreatedMs = 0;
                    saved.LastCompactMs = _now();
                    saved.LastCompactSeq = rotated.State.NextSeq - 1;
                    _store.Save(saved);
                    var done = device is null
                        ? "A conta tem um código de recuperação novo (o antigo deixou de servir); os outros aparelhos continuam ligados sozinhos."
                        : $"«{name}» foi removido. A conta tem um código de recuperação novo (o antigo deixou de servir); os outros aparelhos continuam ligados sozinhos.";
                    _notice = newcomers.Count == 0 ? done
                        : done + $" Apareceu na conta {string.Join(", ", newcomers)}, que não estava na lista: não recebeu a chave nova. Se for seu, junte-o outra vez com o código novo ou por aprovação.";
                }
                await FinishRotationCoreAsync(saved, secret, pending, rotated.State, cancellationToken).ConfigureAwait(false);
            }
            await rotated.PullAsync(cancellationToken).ConfigureAwait(false);
            await RefreshDevicesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChainGoneException)
        {
            // Outro aparelho trocou a chave antes deste: recebe a conta nova (se houver) e pede para tentar outra vez.
            if (saved is not null) await ChainGoneAsync(saved, cancellationToken).ConfigureAwait(false);
            throw new SyncUserException("A conta mudou de chave noutro aparelho entretanto. Veja a lista outra vez e repita, se ainda for preciso.");
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

    // Acaba uma troca de chave guardada a meio (rede em baixo, Éteris fechado): entrega a conta nova aos aparelhos que
    // ficam e apaga a conta antiga.
    private async Task FinishRotationAsync(SavedSync saved, RotationPending rotation, CancellationToken cancellationToken)
    {
        if (saved.Secret is not { } secret || _engine is not { } engine) return;
        using var account = AccountSecret.FromBytes(Kdf.FromBase64Url(secret));
        await FinishRotationCoreAsync(saved, account, rotation, engine.State, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishRotationCoreAsync(SavedSync saved, AccountSecret secret, RotationPending rotation, SyncState state, CancellationToken cancellationToken)
    {
        await SyncEngine.FinishRotationAsync(Transport(), state, secret, rotation, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            if (IsCurrent(saved) && ReferenceEquals(saved.Rotation, rotation))
            {
                saved.Rotation = null;
                _store.Save(saved);
            }
    }

    // «Apagar os meus dados do Éteris»: apaga todos os ficheiros do Éteris no transporte (todas as contas, pedidos de
    // emparelhamento). Não sai da conta Google. A sincronização fica desligada neste aparelho; nos outros para.
    // Lista outra vez depois de apagar (outro aparelho a meio de uma ronda pode ter escrito entretanto), até 4 passagens.
    public async Task DeleteCloudDataAsync(CancellationToken cancellationToken = default)
    {
        await _run.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetRunning(true);
        try
        {
            var transport = Transport();
            for (var pass = 0; pass < 4; pass++)
            {
                var files = await transport.ListAsync(SyncPaths.Root, cancellationToken).ConfigureAwait(false);
                if (files.Count == 0) break;
                foreach (var path in files) await transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            }
            lock (_gate) _hosts.Clear();
            SavedSync saved;
            lock (_gate) saved = _saved ??= new SavedSync();
            Stop(saved, "Os dados do Éteris foram apagados do Google Drive. A sincronização está desligada; os dados deste computador ficaram.", StopKeeps.Nothing);
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
    // entrar é preciso o código ou outro aparelho); sem ela, entrar outra vez retoma sem código — e um código de
    // recuperação ainda por guardar continua à vista até «Já guardei».
    // Não espera pela ronda em curso: ela confere IsCurrent antes de gravar (e o «apagar a chave» não volta atrás).
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
            _needsSignIn = false;
            _waiting = 0;
            _phase = SyncPhase.Off;
            if (forgetKey)
            {
                if (_saved is not null) _saved.Enabled = false; // uma ronda em curso com este objeto já não grava
                _saved = null;
                _store.Delete();
            }
            else if (_saved is not null)
            {
                _saved.Enabled = false;
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

    // Chamado dentro do _gate: o estado desta operação ainda é o deste aparelho e a sincronização continua ligada.
    private bool IsCurrent(SavedSync? saved) => saved is { Enabled: true } && ReferenceEquals(_saved, saved);

    private bool IsCurrentLocked(SavedSync saved)
    {
        lock (_gate) return IsCurrent(saved);
    }

    // A sessão do transporte é de outra conta (só quando se sabem as duas).
    private bool OtherAccount(SavedSync saved) => saved.Account is { } mine && AccountKey?.Invoke() is { } now && now != mine;

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
            if (e is Google.OAuthException { NeedsSignIn: true }) _needsSignIn = true;
            if (_phase == SyncPhase.Starting) _phase = SyncPhase.On;
        }
    }

    private void Notify() => Changed?.Invoke();

    // Falhas que passam sozinhas (rede, sessão, pasta): ficam no estado e o agendador tenta outra vez mais tarde. A
    // autorização da Google retirada ou expirada (OAuthException.NeedsSignIn) também cai aqui, mas fica marcada em
    // Status.NeedsSignIn: o anfitrião para o agendador até o utilizador entrar outra vez.
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
        public bool Approving { get; set; }
    }
}

// Uma ação do utilizador não se pôde cumprir: a mensagem é para lhe mostrar.
public sealed class SyncUserException(string message) : Exception(message);
