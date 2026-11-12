using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Merge;
using Eteris.Sync.Model;
using Eteris.Sync.Pairing;
using Eteris.Sync.Secrets;
using Eteris.Sync.Transport;

namespace Eteris.Sync.Engine;

// A conta deixou de existir neste transporte (outro aparelho fez «nova cadeia» ou a pasta foi apagada).
public sealed class ChainGoneException(string message) : Exception(message);

public sealed record PullResult(int Applied, IReadOnlyDictionary<string, string> Pending, bool NeedsUpdate, bool Rebased);

// A conta nova deixada a este aparelho numa cadeia que desapareceu (ReceiveRekeyAsync). Conflict: há mensagens, mas
// contradizem-se (dois aparelhos a dar um ao outro como removido, ou contas novas diferentes) — não se escolhe sozinho.
public sealed record RekeyResult(AccountSecret? Secret, string From, string FromName, IReadOnlyList<string>? Devices, IReadOnlyList<string> Paths,
    bool Conflict) : IDisposable
{
    public void Dispose() => Secret?.Dispose();
}

// Motor da sincronização: réplica local + registo só de acréscimo por aparelho num transporte qualquer.
//
//   alterações locais → caixa de saída → Push: um lote imutável novo (d/<aparelho>/<n>), cifrado
//   Pull: lê os lotes novos dos outros aparelhos, por ordem, e funde (LWW por HLC; notas a três)
//   Compact: instantâneo do estado (s/…) e limpeza dos lotes que todos já leram
//
// Um lote que não se decifra (adulterado, de uma versão mais recente ou ainda a meio de chegar pela nuvem) NUNCA é
// marcado como lido: fica pendente e é tentado na próxima vez; os lotes seguintes desse aparelho esperam por ele.
public sealed class SyncEngine
{
    private readonly ISyncTransport _transport;
    private readonly KeyRing _keys;
    private readonly Func<long> _now;
    private readonly HybridClock _clock;

    private SyncEngine(ISyncTransport transport, KeyRing keys, SyncState state, Func<long> now, SyncOptions? options)
    {
        _transport = transport;
        _keys = keys;
        _now = now;
        State = state;
        Options = options ?? new SyncOptions();
        _clock = new HybridClock(state.Device.Id, now);
        _clock.Restore(state.Clock);
        state.DeviceNames[state.Device.Id] = state.Device.Name;
        state.Seals = keys.Seals;
        if (state.DeviceKey is null)
        {
            using var key = PairingProtocol.NewKey();
            state.DeviceKey = Kdf.Base64Url(key.ExportPkcs8PrivateKey());
        }
    }

    // A chave pública estática deste aparelho (vai no estado do aparelho, cifrada como o resto).
    public string DevicePublicKey
    {
        get
        {
            using var key = OpenDeviceKey(State);
            return Kdf.Base64Url(PairingProtocol.PublicKey(key));
        }
    }

    private static ECDiffieHellman OpenDeviceKey(SyncState state)
    {
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        key.ImportPkcs8PrivateKey(Kdf.FromBase64Url(state.DeviceKey ?? throw new InvalidOperationException("Aparelho sem chave.")), out _);
        return key;
    }

    // Depois de receber a conta nova (ReceiveRekeyAsync), o aparelho continua com a mesma chave e com as chaves fixadas
    // dos aparelhos que ficaram na conta nova («devices», da mensagem); as dos outros (o removido) não passam.
    public void AdoptDeviceKeys(SyncState previous, IReadOnlyCollection<string>? devices = null)
    {
        if (previous.DeviceKey is { } key) State.DeviceKey = key;
        foreach (var (device, pub) in previous.PeerKeys)
            if (devices is null || devices.Contains(device)) State.PeerKeys.TryAdd(device, pub);
    }

    // Depois de receber a conta nova: o que ESTE aparelho escreveu na conta antiga (com a hora original, lápides
    // incluídas) volta à caixa de saída, e o histórico das notas também. O aparelho que trocou a chave pode não ter lido
    // os últimos lotes deste (a cadeia antiga foi apagada com eles): assim o LWW decide contra o instantâneo dele, em vez
    // de este aparelho aplicar esse instantâneo por cima do que tinha mudado (e apagar, por exemplo, um favorito novo).
    public void RestageOwn(SyncState previous)
    {
        foreach (var version in previous.NotesHistory) Remember(version, outgoing: false);
        foreach (var record in previous.Records.Values.Where(r => r.Version.Node == Device.Id))
        {
            // Os registos secretos mudam de Key e de selo (as chaves dos selos da cadeia antiga ficaram no estado dela);
            // sem elas (estado de um Éteris anterior), não passam: o perfil volta a enviá-los se mudarem.
            var moved = Move(record, previous.Seals, this);
            if (moved is null) continue;
            var id = SyncState.RecordKey(moved.Collection, moved.Id);
            _clock.Observe(moved.Version);
            if (State.Records.TryGetValue(id, out var current) && current.Version >= moved.Version) continue;
            State.Records[id] = moved;
            State.Outbox[id] = moved;
        }
        State.Clock = _clock.Last;
    }

    // Aparelhos que já escreveram nesta conta e cuja chave pública ainda não está fixada (DevicesAsync fixa-as).
    public bool HasUnpinnedPeers
        => State.Read.Keys.Concat(State.DeviceNames.Keys).Any(d => d != Device.Id && !State.PeerKeys.ContainsKey(d));

    public SyncState State { get; }

    public SyncOptions Options { get; }

    public DeviceIdentity Device => State.Device;

    // Os grupos ligados por inteiro neste aparelho (quem usa o motor diz; null = não diz, como um Éteris antigo): vão no
    // estado do aparelho, cifrado, para a lista de aparelhos dos outros. Só memória: o agente põe-nos em cada ronda.
    public IReadOnlyList<string>? DeviceGroups { get; set; }

    // O nome novo segue no próximo estado do aparelho e nos próximos lotes (ambos cifrados). O Id e o tipo não mudam.
    public void Rename(string name)
    {
        State.Device = State.Device with { Name = name };
        State.DeviceNames[State.Device.Id] = name;
    }

    public string ChainId => _keys.ChainId;

    public ISyncTransport Transport => _transport;

    // Cria a conta (cadeia) neste transporte: escreve o registo de chaves. Falha se já existir.
    public static async Task<SyncEngine> CreateAsync(ISyncTransport transport, AccountSecret secret, DeviceIdentity device, Func<long> now,
        SyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        var path = SyncPaths.Keys(secret.ChainId);
        if (await transport.ReadAsync(path, cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Esta conta já existe neste transporte: use «Juntar».");
        var keys = KeyRing.Create(secret, now());
        await transport.WriteAsync(path, keys.Envelope.ToBytes(), cancellationToken).ConfigureAwait(false);
        return new SyncEngine(transport, keys, new SyncState { Device = device, ChainId = keys.ChainId }, now, options);
    }

    // Junta este aparelho a uma conta que já existe (código de recuperação ou emparelhamento).
    public static async Task<SyncEngine> JoinAsync(ISyncTransport transport, AccountSecret secret, DeviceIdentity device, Func<long> now,
        SyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        var keys = await OpenKeysAsync(transport, secret, cancellationToken).ConfigureAwait(false);
        return new SyncEngine(transport, keys, new SyncState { Device = device, ChainId = keys.ChainId }, now, options);
    }

    // Retoma com o estado guardado (arranque seguinte).
    public static async Task<SyncEngine> ResumeAsync(ISyncTransport transport, AccountSecret secret, SyncState state, Func<long> now,
        SyncOptions? options = null, CancellationToken cancellationToken = default)
    {
        var keys = await OpenKeysAsync(transport, secret, cancellationToken).ConfigureAwait(false);
        if (state.ChainId != keys.ChainId) throw new WrongCodeException("O estado guardado é de outra conta.");
        var engine = new SyncEngine(transport, keys, state, now, options);
        await engine.CatchUpOwnBatchesAsync(cancellationToken).ConfigureAwait(false);
        return engine;
    }

    // O estado guardado pode ser uma cópia antiga (reposta de uma cópia de segurança, ou de outro disco) com o mesmo
    // aparelho: o NextSeq voltava atrás e os lotes novos reutilizavam números que os outros já deram como lidos —
    // ignorados em silêncio. Ao retomar, o que está no transporte manda: lotes e instantâneos deste aparelho com número
    // igual ou maior do que o NextSeq guardado são de depois desta cópia. São lidos (são deste aparelho; o estado
    // reposto não os tem), o NextSeq passa à frente deles, e um lote pendente com um número já usado volta à caixa
    // de saída para seguir com um número novo.
    private async Task CatchUpOwnBatchesAsync(CancellationToken cancellationToken)
    {
        var own = new SortedSet<long>();
        foreach (var path in await _transport.ListAsync(SyncPaths.Batches(ChainId) + Device.Id + "/", cancellationToken).ConfigureAwait(false))
            if (SyncPaths.TryParseBatch(ChainId, path, out var device, out var seq) && device == Device.Id) own.Add(seq);
        var max = own.Count > 0 ? own.Max : 0;
        foreach (var path in await _transport.ListAsync(SyncPaths.Snapshots(ChainId), cancellationToken).ConfigureAwait(false))
            if (SyncPaths.TryParseSnapshot(ChainId, path, out var device, out var seq) && device == Device.Id) max = Math.Max(max, seq);
        if (max < State.NextSeq) return;

        var restoredNext = State.NextSeq;
        foreach (var seq in own.Where(s => s >= restoredNext))
        {
            try
            {
                var bytes = await _transport.ReadAsync(SyncPaths.Batch(ChainId, Device.Id, seq), cancellationToken).ConfigureAwait(false);
                if (bytes is null) continue;
                var context = SyncPaths.BatchContext(Device.Id, seq);
                var payload = Json.Parse<BatchPayload>(SealedFile.Open(_keys.FileKey(SealedFile.Batch, context), SealedFile.Batch, ChainId, context, bytes));
                if (payload.Device != Device.Id || payload.Seq != seq) continue;
                foreach (var version in payload.NotesHistory ?? []) Remember(version, outgoing: false);
                foreach (var record in payload.Records)
                {
                    Merge(record);
                    State.SentIn[SyncState.RecordKey(record.Collection, record.Id)] = seq;
                }
                State.OwnBatchTimes[seq] = payload.CreatedMs;
            }
            catch (Exception e) when (e is CryptographicException or FormatException or UnsupportedFormatException) { }
        }
        if (State.Pending is { } pending && pending.Seq <= max)
        {
            foreach (var record in pending.Records)
            {
                var id = SyncState.RecordKey(record.Collection, record.Id);
                if (!State.Outbox.TryGetValue(id, out var newer) || newer.Version < record.Version) State.Outbox[id] = record;
            }
            State.OutboxNotes.InsertRange(0, pending.Notes);
            State.Pending = null;
        }
        State.NextSeq = max + 1;
        State.Clock = _clock.Last;
    }

    private static async Task<KeyRing> OpenKeysAsync(ISyncTransport transport, AccountSecret secret, CancellationToken cancellationToken)
    {
        var bytes = await transport.ReadAsync(SyncPaths.Keys(secret.ChainId), cancellationToken).ConfigureAwait(false)
                    ?? throw new ChainGoneException("Não há nenhuma conta com este código neste transporte (ou foi substituída por uma nova cadeia).");
        return KeyRing.Open(secret, KeyEnvelope.Parse(bytes));
    }

    // ---- alterações locais ----------------------------------------------------------------------------------------

    public Hlc Put(string collection, string key, JsonObject data) => Stage(collection, key, data, deleted: false);

    public Hlc Delete(string collection, string key) => Stage(collection, key, null, deleted: true);

    public SyncRecord? Get(string collection, string key)
        => State.Records.TryGetValue(SyncState.RecordKey(collection, _keys.OpaqueId(collection, key)), out var r) ? r : null;

    public IReadOnlyList<SyncRecord> Records(string collection)
        => State.Records.Values.Where(r => r.Collection == collection && !r.Deleted).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();

    // Abas abertas: instantâneo deste aparelho (os outros veem-no em «Abas noutros aparelhos»; nada se funde).
    public Hlc PublishTabs(JsonObject tabs) => Put(Collections.Tabs, Device.Id, tabs);

    public string Notes => Get(Collections.Notes, Collections.NotesKey)?.Text("text") ?? string.Empty;

    public Hlc SetNotes(string text)
    {
        var current = Get(Collections.Notes, Collections.NotesKey);
        var parents = current is null ? [] : new[] { NotesId(current) };
        var version = _clock.Now();
        var record = NotesRecord(text, parents, version);
        Remember(new NotesVersion(NotesId(text, parents), parents, version, text), outgoing: true);
        Accept(record, outgoing: true);
        return version;
    }

    private Hlc Stage(string collection, string key, JsonObject? data, bool deleted)
    {
        if (collection == Collections.Notes) throw new ArgumentException("As notas mudam-se com SetNotes.", nameof(collection));
        if (Collections.IsSecret(collection)) throw new ArgumentException("Os registos secretos mudam-se com PutSecret e DeleteSecret.", nameof(collection));
        return StageRecord(collection, key, _keys.OpaqueId(collection, key), deleted ? null : (JsonObject)data!.DeepClone(), deleted);
    }

    private Hlc StageRecord(string collection, string key, string id, JsonObject? data, bool deleted)
    {
        var version = _clock.Now();
        Accept(new SyncRecord { Collection = collection, Id = id, Key = key, Version = version, Deleted = deleted, Data = data }, outgoing: true);
        return version;
    }

    // ---- registos secretos (Secrets/SecretSeal) ---------------------------------------------------------------------

    // A chave opaca de um registo secreto nesta cadeia.
    public string SecretKey(string collection, string naturalKey) => _keys.Seals.OpaqueKey(collection, naturalKey);

    // Grava um registo secreto selado. Os campos que este Éteris não conhece (de um Éteris mais recente, dentro do selo
    // atual) seguem tal e qual.
    public Hlc PutSecret(string collection, SecretItem item)
    {
        if (!Collections.IsSecret(collection)) throw new ArgumentException("Coleção que não é secreta.", nameof(collection));
        var key = SecretKey(collection, item.NaturalKey);
        var data = (JsonObject)item.Data.DeepClone();
        if (Get(collection, key) is { Deleted: false } existing && OpenSecret(existing) is { } old)
            foreach (var (name, value) in old.Data)
                if (!SecretSchemas.IsKnown(collection, name) && !data.ContainsKey(name)) data[name] = value?.DeepClone();
        var id = _keys.OpaqueId(collection, key);
        return StageRecord(collection, key, id, _keys.Seals.Seal(collection, id, new SecretItem(item.NaturalKey, data)), deleted: false);
    }

    // Apaga um registo secreto: a lápide leva só a chave natural, selada (para a «nova cadeia» a poder levar).
    public Hlc DeleteSecret(string collection, string naturalKey)
    {
        if (!Collections.IsSecret(collection)) throw new ArgumentException("Coleção que não é secreta.", nameof(collection));
        var key = SecretKey(collection, naturalKey);
        var id = _keys.OpaqueId(collection, key);
        return StageRecord(collection, key, id, _keys.Seals.Seal(collection, id, new SecretItem(naturalKey, new JsonObject())), deleted: true);
    }

    // Abre o selo de um registo desta cadeia. null = não abre (adulterado, movido, de outra cadeia, formato mais recente):
    // o registo fica de fora (não se aplica) e conta como erro, sem parar a ronda.
    public SecretItem? OpenSecret(SyncRecord record) => Open(record, _keys.Seals);

    private static SecretItem? Open(SyncRecord record, SealKeys? seals)
    {
        if (seals is null || !Collections.IsSecret(record.Collection)) return null;
        try
        {
            var item = seals.Open(record.Collection, record.Id, record.Data);
            return seals.OpaqueKey(record.Collection, item.NaturalKey) == record.Key ? item : null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    // Um registo levado para outra cadeia: o ID opaco é refeito; os secretos são abertos com as chaves de onde vêm e
    // selados de novo com as da cadeia nova (Key opaca nova). null = um secreto que não abre.
    private static SyncRecord? Move(SyncRecord record, SealKeys? from, SyncEngine to)
    {
        if (!Collections.IsSecret(record.Collection)) return record with { Id = to._keys.OpaqueId(record.Collection, record.Key) };
        if (Open(record, from) is not { } item) return null;
        var key = to.SecretKey(record.Collection, item.NaturalKey);
        var id = to._keys.OpaqueId(record.Collection, key);
        return record with { Key = key, Id = id, Data = to._keys.Seals.Seal(record.Collection, id, item) };
    }

    private void Accept(SyncRecord record, bool outgoing)
    {
        var id = SyncState.RecordKey(record.Collection, record.Id);
        State.Records[id] = record;
        if (outgoing) State.Outbox[id] = record;
        else if (State.Outbox.TryGetValue(id, out var mine) && mine.Version < record.Version) State.Outbox.Remove(id);
        State.Clock = _clock.Last;
    }

    // ---- envio ----------------------------------------------------------------------------------------------------

    // Envia o que há na caixa de saída como um lote novo. Devolve o número do lote, ou null se não havia nada.
    public async Task<long?> PushAsync(CancellationToken cancellationToken = default)
    {
        if (State.Pending is null && State.Outbox.Count == 0) return null;
        long sent = 0;
        if (State.Pending is { } retry)
        {
            await WriteBatchAsync(retry, cancellationToken).ConfigureAwait(false);
            sent = retry.Seq;
        }
        if (State.Outbox.Count > 0)
        {
            var batch = new PendingBatch(State.NextSeq, State.Outbox.Values.OrderBy(r => r.Version).ToList(), State.OutboxNotes.ToList(), _now());
            State.Pending = batch;
            State.NextSeq++;
            State.Outbox.Clear();
            State.OutboxNotes.Clear();
            await WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            sent = batch.Seq;
        }
        return sent;
    }

    private async Task WriteBatchAsync(PendingBatch batch, CancellationToken cancellationToken)
    {
        var context = SyncPaths.BatchContext(Device.Id, batch.Seq);
        var payload = new BatchPayload
        {
            Device = Device.Id,
            DeviceName = Device.Name,
            Seq = batch.Seq,
            CreatedMs = batch.CreatedMs,
            Records = batch.Records,
            NotesHistory = batch.Notes.Count > 0 ? batch.Notes : null,
        };
        var file = SealedFile.Seal(_keys.FileKey(SealedFile.Batch, context), SealedFile.Batch, ChainId, _keys.Generation, context, Json.Bytes(payload),
            compress: !Collections.HasTabs(batch.Records));
        await _transport.WriteAsync(SyncPaths.Batch(ChainId, Device.Id, batch.Seq), file, cancellationToken).ConfigureAwait(false);
        State.OwnBatchTimes[batch.Seq] = batch.CreatedMs;
        foreach (var record in batch.Records) State.SentIn[SyncState.RecordKey(record.Collection, record.Id)] = batch.Seq;
        State.Pending = null;
    }

    // ---- receção --------------------------------------------------------------------------------------------------

    public async Task<PullResult> PullAsync(CancellationToken cancellationToken = default)
    {
        var keysFile = await _transport.ReadAsync(SyncPaths.Keys(ChainId), cancellationToken).ConfigureAwait(false)
                       ?? throw new ChainGoneException("Esta conta deixou de existir: outro aparelho criou uma nova cadeia. Peça o código novo.");
        // «Atualize o Éteris» só quando o registo de chaves, AUTENTICADO pela chave da conta, anuncia um formato mais
        // recente. A versão no cabeçalho de um lote é lida antes de o decifrar: quem controla o transporte podia mudá-la
        // e pôr o aparelho a pedir uma atualização falsa (alavanca para «descarregue esta atualização»).
        var announced = AnnouncedNewerFormat(keysFile);

        var files = await _transport.ListAsync(SyncPaths.Chain(ChainId), cancellationToken).ConfigureAwait(false);
        var batches = new Dictionary<string, SortedSet<long>>(StringComparer.Ordinal);
        var snapshots = new List<(string Device, long Seq, string Path)>();
        foreach (var path in files)
        {
            if (SyncPaths.TryParseBatch(ChainId, path, out var device, out var seq))
            {
                if (device == Device.Id) continue;
                if (!batches.TryGetValue(device, out var set)) batches[device] = set = [];
                set.Add(seq);
            }
            else if (SyncPaths.TryParseSnapshot(ChainId, path, out var snapDevice, out var snapSeq)) snapshots.Add((snapDevice, snapSeq, path));
        }

        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        var needsUpdate = announced;
        var rebased = false;
        var now = _now();
        var fresh = State.Read.Count == 0 && State.Records.Count == State.Outbox.Count;
        var stale = State.LastPullMs > 0 && now - State.LastPullMs > (long)Options.TombstoneTtl.TotalMilliseconds;
        // Lotes que já não existem (limpos depois de um instantâneo) e que este aparelho nunca leu.
        var gap = batches.Any(b => b.Value.Min > State.Read.GetValueOrDefault(b.Key) + 1);

        if ((fresh || stale || gap) && snapshots.Count > 0)
        {
            var (loaded, error) = await LoadSnapshotsAsync(snapshots, cancellationToken).ConfigureAwait(false);
            if (loaded.Count > 0 && (fresh || stale || Covers(loaded, batches)))
            {
                Rebase(loaded);
                rebased = true;
            }
        }

        var applied = 0;
        foreach (var (device, seqs) in batches.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            foreach (var seq in seqs)
            {
                var read = State.Read.GetValueOrDefault(device);
                if (seq <= read) continue;
                if (seq != read + 1)
                {
                    // Falta um lote no meio: a nuvem ainda não o trouxe, ou foi limpo e não há instantâneo que o cubra.
                    pending[device] = $"à espera do lote {read + 1}";
                    break;
                }
                try
                {
                    var bytes = await _transport.ReadAsync(SyncPaths.Batch(ChainId, device, seq), cancellationToken).ConfigureAwait(false)
                                ?? throw new FormatException("o lote desapareceu");
                    var context = SyncPaths.BatchContext(device, seq);
                    var payload = Json.Parse<BatchPayload>(SealedFile.Open(_keys.FileKey(SealedFile.Batch, context), SealedFile.Batch, ChainId, context, bytes));
                    if (payload.Device != device || payload.Seq != seq) throw new FormatException("lote fora do lugar");
                    if (payload.DeviceName.Length > 0) State.DeviceNames[device] = payload.DeviceName;
                    foreach (var version in payload.NotesHistory ?? []) Remember(version, outgoing: false);
                    foreach (var record in payload.Records) applied += Merge(record) ? 1 : 0;
                    State.Read[device] = seq;
                }
                catch (UnsupportedFormatException)
                {
                    pending[device] = announced
                        ? $"lote {seq} de um Éteris mais recente: atualize o Éteris"
                        : $"lote {seq} num formato que este Éteris não conhece (fica à espera)";
                    break;
                }
                catch (Exception e) when (e is CryptographicException or FormatException)
                {
                    pending[device] = $"lote {seq} recusado ({e.Message})";
                    break;
                }
            }
        }

        State.LastPullMs = now;
        State.Clock = _clock.Last;
        await WriteDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        return new PullResult(applied, pending, needsUpdate, rebased);
    }

    public async Task<PullResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var result = await PullAsync(cancellationToken).ConfigureAwait(false);
        await PushAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private bool AnnouncedNewerFormat(byte[] keysFile)
    {
        try
        {
            var envelope = KeyEnvelope.Parse(keysFile);
            return envelope.Format > KeyEnvelope.CurrentFormat && _keys.Authenticates(envelope);
        }
        catch (FormatException) { return false; }
    }

    private static bool Covers(List<SnapshotPayload> snapshots, Dictionary<string, SortedSet<long>> batches)
        => batches.All(b => snapshots.Max(s => s.Covers.GetValueOrDefault(b.Key)) + 1 >= b.Value.Min);

    private async Task<(List<SnapshotPayload> Snapshots, Exception? Error)> LoadSnapshotsAsync(List<(string Device, long Seq, string Path)> snapshots,
        CancellationToken cancellationToken)
    {
        var list = new List<SnapshotPayload>();
        Exception? error = null;
        foreach (var (device, seq, path) in snapshots)
        {
            try
            {
                var bytes = await _transport.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes is null) continue;
                var context = SyncPaths.SnapshotContext(device, seq);
                var snapshot = Json.Parse<SnapshotPayload>(SealedFile.Open(_keys.FileKey(SealedFile.Snapshot, context), SealedFile.Snapshot, ChainId, context, bytes));
                if (snapshot.Device == device && snapshot.Seq == seq) list.Add(snapshot);
            }
            catch (Exception e) when (e is CryptographicException or FormatException or UnsupportedFormatException)
            {
                error = e;
            }
        }
        return (list, error);
    }

    // Recomeça dos instantâneos: o estado sincronizado passa a ser a junção deles (cada um vale até ao que cobre),
    // mais o que este aparelho já enviou e nenhum instantâneo cobre, e por cima o que ainda não foi enviado daqui. É o
    // que impede um aparelho ausente há mais do que o prazo das lápides de ressuscitar apagados: um registo vivo num
    // instantâneo velho que falta num mais recente QUE VIU TUDO O QUE O VELHO VIU, e que é mais antigo do que o
    // horizonte das lápides deste, foi apagado e a lápide já saiu — não volta.
    private void Rebase(List<SnapshotPayload> snapshots)
    {
        var unsent = State.Outbox.Values.Concat(State.Pending?.Records ?? []).ToList();
        // Enviados daqui depois do que os instantâneos cobrem deste aparelho: o PullAsync nunca lê os lotes próprios,
        // por isso, sem isto, desapareciam deste aparelho (e a compactação seguinte apagava-os de todo).
        var covered = snapshots.Max(s => s.Covers.GetValueOrDefault(Device.Id));
        var sentLater = State.SentIn.Where(s => s.Value > covered)
            .Select(s => State.Records.TryGetValue(s.Key, out var record) && record.Version.Node == Device.Id ? record : null)
            .OfType<SyncRecord>().ToList();
        State.Records = new Dictionary<string, SyncRecord>(StringComparer.Ordinal);
        State.Outbox = new Dictionary<string, SyncRecord>(StringComparer.Ordinal);
        State.Read = new Dictionary<string, long>(StringComparer.Ordinal);
        var ordered = snapshots.OrderBy(s => s.CreatedMs).ToList();
        var present = ordered.ToDictionary(s => s, s => s.Records.Select(r => SyncState.RecordKey(r.Collection, r.Id)).ToHashSet(StringComparer.Ordinal));
        foreach (var snapshot in ordered)
        {
            foreach (var version in snapshot.NotesHistory ?? []) Remember(version, outgoing: false);
            foreach (var record in snapshot.Records)
            {
                var key = SyncState.RecordKey(record.Collection, record.Id);
                var purged = ordered.Any(newer => newer.CreatedMs > snapshot.CreatedMs && record.Version.Wall < newer.TombstoneHorizonMs
                                                  && Dominates(newer, snapshot) && !present[newer].Contains(key));
                if (!purged) Merge(record);
            }
            foreach (var (device, seq) in snapshot.Covers)
                if (device != Device.Id) State.Read[device] = Math.Max(State.Read.GetValueOrDefault(device), seq);
        }
        foreach (var record in sentLater) Merge(record);
        foreach (var record in unsent) Merge(record, local: true);
    }

    // O instantâneo mais recente viu tudo o que o mais antigo viu (de todos os aparelhos): só então a falta de um
    // registo nele quer dizer «apagado». Senão pode faltar só porque o autor ainda não leu o lote que o criou.
    private static bool Dominates(SnapshotPayload newer, SnapshotPayload older)
        => older.Covers.All(c => newer.Covers.GetValueOrDefault(c.Key) >= c.Value);

    // Funde um registo que veio de fora. Devolve true se o estado local mudou.
    private bool Merge(SyncRecord incoming, bool local = false)
    {
        _clock.Observe(incoming.Version);
        if (incoming.Collection == Collections.Notes) return MergeNotes(incoming, local);
        var id = SyncState.RecordKey(incoming.Collection, incoming.Id);
        if (State.Records.TryGetValue(id, out var current) && current.Version >= incoming.Version) return false;
        Accept(incoming, outgoing: local);
        return true;
    }

    // ---- notas: fusão a três ----------------------------------------------------------------------------------------

    private bool MergeNotes(SyncRecord incoming, bool local)
    {
        var id = SyncState.RecordKey(incoming.Collection, incoming.Id);
        var remoteText = incoming.Text("text") ?? string.Empty;
        var remoteHash = NotesId(incoming);
        Remember(new NotesVersion(remoteHash, Parents(incoming), incoming.Version, remoteText), outgoing: false);
        if (!State.Records.TryGetValue(id, out var current))
        {
            Accept(incoming, outgoing: local);
            return true;
        }
        var localHash = NotesId(current);
        if (localHash == remoteHash || IsAncestor(remoteHash, localHash))
        {
            // O mesmo texto, ou uma versão que este aparelho já tem incorporada: fica a de HLC maior (LWW).
            if (incoming.Version > current.Version && localHash == remoteHash) { Accept(incoming, outgoing: local); return true; }
            return false;
        }
        if (IsAncestor(localHash, remoteHash))
        {
            Accept(incoming, outgoing: local);
            return true;
        }
        // Edições concorrentes: base comum + as duas versões, a mais antiga (por HLC) primeiro, em todos os aparelhos.
        var baseText = CommonAncestorText(localHash, remoteHash);
        var (older, newer) = current.Version < incoming.Version ? (current, incoming) : (incoming, current);
        var merged = NotesMerge.Merge(baseText, older.Text("text") ?? string.Empty, newer.Text("text") ?? string.Empty,
            Label(older.Version.Node), Label(newer.Version.Node)).Text;
        var parents = new[] { localHash, remoteHash }.Order(StringComparer.Ordinal).ToArray();
        var version = _clock.Now();
        Remember(new NotesVersion(NotesId(merged, parents), parents, version, merged), outgoing: true);
        Accept(NotesRecord(merged, parents, version), outgoing: true);
        return true;
    }

    // O nome é recebido (escolhido pelo outro aparelho): entra no texto das notas limpo, como em todo o lado onde se
    // mostra um nome que chegou de fora.
    private string Label(string node)
        => (State.DeviceNames.TryGetValue(node, out var name) ? DeviceNameText.Clean(name) : null) ?? "aparelho " + node[..Math.Min(6, node.Length)];

    private SyncRecord NotesRecord(string text, IReadOnlyList<string> parents, Hlc version)
        => new()
        {
            Collection = Collections.Notes,
            Id = _keys.OpaqueId(Collections.Notes, Collections.NotesKey),
            Key = Collections.NotesKey,
            Version = version,
            Data = new JsonObject { ["text"] = text, ["parents"] = new JsonArray(parents.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) },
        };

    private static string[] Parents(SyncRecord record)
        => record.Data?["parents"] is JsonArray array ? array.Select(n => n?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToArray() : [];

    // Identidade de uma versão das notas: o texto E de onde veio (como um commit do git). Voltar a um texto antigo é
    // uma versão nova, não a antiga — senão desfazer uma alteração parecia «já visto» e não chegava aos outros.
    public static string NotesId(string text, IEnumerable<string> parents)
        => Kdf.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text + "\n\u0000" + string.Join(',', parents.Order(StringComparer.Ordinal)))).AsSpan(0, 16));

    private static string NotesId(SyncRecord record) => NotesId(record.Text("text") ?? string.Empty, Parents(record));

    private void Remember(NotesVersion version, bool outgoing)
    {
        if (State.NotesHistory.All(v => v.Hash != version.Hash || v.Version != version.Version)) State.NotesHistory.Add(version);
        if (outgoing) State.OutboxNotes.Add(version);
        if (State.NotesHistory.Count > Options.NotesHistoryLimit)
            State.NotesHistory = State.NotesHistory.OrderByDescending(v => v.Version).Take(Options.NotesHistoryLimit).ToList();
    }

    private bool IsAncestor(string ancestor, string of)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([of]);
        while (queue.Count > 0)
        {
            var hash = queue.Dequeue();
            if (!seen.Add(hash)) continue;
            foreach (var version in State.NotesHistory.Where(v => v.Hash == hash))
                foreach (var parent in version.Parents)
                {
                    if (parent == ancestor) return true;
                    queue.Enqueue(parent);
                }
        }
        return false;
    }

    private HashSet<string> Ancestry(string hash)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([hash]);
        while (queue.Count > 0)
        {
            var h = queue.Dequeue();
            if (!seen.Add(h)) continue;
            foreach (var version in State.NotesHistory.Where(v => v.Hash == h))
                foreach (var parent in version.Parents) queue.Enqueue(parent);
        }
        return seen;
    }

    // Base da fusão: o antepassado comum mais recente (por HLC; desempate pelo hash), igual em todos os aparelhos que
    // viram as mesmas versões. Sem antepassado comum, a base é o texto vazio.
    private string CommonAncestorText(string a, string b)
    {
        var common = Ancestry(a);
        common.IntersectWith(Ancestry(b));
        var best = State.NotesHistory.Where(v => common.Contains(v.Hash))
            .OrderByDescending(v => v.Version).ThenBy(v => v.Hash, StringComparer.Ordinal).FirstOrDefault();
        return best?.Text ?? string.Empty;
    }

    // ---- estado do aparelho, compactação e nova cadeia --------------------------------------------------------------

    private async Task WriteDeviceStateAsync(CancellationToken cancellationToken)
    {
        var context = SyncPaths.StateContext(Device.Id);
        var payload = new DeviceStatePayload
        {
            Device = Device.Id,
            Name = Device.Name,
            Kind = Device.Kind,
            Read = new Dictionary<string, long>(State.Read, StringComparer.Ordinal),
            LastSyncMs = State.LastPullMs,
            PublicKey = DevicePublicKey,
            Groups = DeviceGroups,
        };
        var file = SealedFile.Seal(_keys.FileKey(SealedFile.DeviceState, context), SealedFile.DeviceState, ChainId, _keys.Generation, context, Json.Bytes(payload));
        await _transport.WriteAsync(SyncPaths.State(ChainId, Device.Id), file, cancellationToken).ConfigureAwait(false);
    }

    // Aparelhos da conta (lidos dos seus ficheiros de estado, cifrados): nome, tipo, última sincronização.
    public async Task<IReadOnlyList<DeviceStatePayload>> DevicesAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<DeviceStatePayload>();
        foreach (var path in await _transport.ListAsync(SyncPaths.States(ChainId), cancellationToken).ConfigureAwait(false))
        {
            if (!SyncPaths.TryParseState(ChainId, path, out var device)) continue;
            try
            {
                var bytes = await _transport.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes is null) continue;
                var context = SyncPaths.StateContext(device);
                var state = Json.Parse<DeviceStatePayload>(SealedFile.Open(_keys.FileKey(SealedFile.DeviceState, context), SealedFile.DeviceState, ChainId, context, bytes));
                if (state.Device != device) continue;
                list.Add(state);
                // O nome também se conhece por aqui (um aparelho que ainda não enviou nenhum lote).
                if (device != Device.Id && state.Name.Length > 0) State.DeviceNames[device] = state.Name;
                // Fixada na primeira vez (TOFU): uma chave trocada depois não substitui a fixada.
                if (device != Device.Id && state.PublicKey is { } pub && !State.PeerKeys.ContainsKey(device))
                {
                    try
                    {
                        using (PairingProtocol.ImportPublic(Kdf.FromBase64Url(pub))) { }
                        State.PeerKeys[device] = pub;
                    }
                    catch (Exception e) when (e is PairingException or FormatException) { }
                }
            }
            catch (Exception e) when (e is CryptographicException or FormatException or UnsupportedFormatException) { }
        }
        return list;
    }

    // Instantâneo do estado + limpeza: apaga as lápides mais velhas do que o prazo e os lotes deste aparelho que
    // todos os outros já leram (ou que são mais velhos do que o prazo e o instantâneo cobre).
    public async Task<long> CompactAsync(CancellationToken cancellationToken = default)
    {
        await PushAsync(cancellationToken).ConfigureAwait(false);
        var now = _now();
        var horizon = now - (long)Options.TombstoneTtl.TotalMilliseconds;
        foreach (var (key, record) in State.Records.Where(r => r.Value.Deleted && r.Value.Version.Wall < horizon).ToList())
            State.Records.Remove(key);

        var mine = State.NextSeq - 1;
        var covers = new Dictionary<string, long>(State.Read, StringComparer.Ordinal) { [Device.Id] = mine };
        var snapshot = new SnapshotPayload
        {
            Device = Device.Id,
            Seq = mine,
            CreatedMs = now,
            Covers = covers,
            TombstoneHorizonMs = horizon,
            Records = State.Records.Values.OrderBy(r => r.Collection, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(),
            NotesHistory = State.NotesHistory,
        };
        var context = SyncPaths.SnapshotContext(Device.Id, mine);
        var file = SealedFile.Seal(_keys.FileKey(SealedFile.Snapshot, context), SealedFile.Snapshot, ChainId, _keys.Generation, context, Json.Bytes(snapshot),
            compress: !Collections.HasTabs(snapshot.Records));
        await _transport.WriteAsync(SyncPaths.Snapshot(ChainId, Device.Id, mine), file, cancellationToken).ConfigureAwait(false);
        // O que seguiu nos lotes até «mine» está agora neste instantâneo.
        foreach (var key in State.SentIn.Where(s => s.Value <= mine).Select(s => s.Key).ToList()) State.SentIn.Remove(key);

        // Instantâneos antigos deste aparelho saem.
        foreach (var path in await _transport.ListAsync(SyncPaths.Snapshots(ChainId), cancellationToken).ConfigureAwait(false))
            if (SyncPaths.TryParseSnapshot(ChainId, path, out var device, out var seq) && device == Device.Id && seq < mine)
                await _transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);

        // Lotes que já ninguém precisa: lidos por todos os outros aparelhos conhecidos, ou mais velhos do que o prazo —
        // estes só porque o instantâneo acabado de escrever os cobre (Covers[este] = mine) e tem os registos deles (o
        // Rebase já não os perde: SentIn).
        var others = (await DevicesAsync(cancellationToken).ConfigureAwait(false)).Where(d => d.Device != Device.Id).ToList();
        var acked = others.Count == 0 ? mine : others.Min(d => d.Read.GetValueOrDefault(Device.Id));
        foreach (var path in await _transport.ListAsync(SyncPaths.Batches(ChainId) + Device.Id + "/", cancellationToken).ConfigureAwait(false))
        {
            if (!SyncPaths.TryParseBatch(ChainId, path, out _, out var seq) || seq > mine) continue;
            var old = State.OwnBatchTimes.TryGetValue(seq, out var created) && created < horizon;
            if (seq <= acked || old)
            {
                await _transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                State.OwnBatchTimes.Remove(seq);
            }
        }
        return mine;
    }

    // «Nova cadeia» (aparelho perdido ou removido, código exposto): segredo novo, DEK nova, o estado todo recifrado na
    // pasta da cadeia nova e a antiga apagada. Os aparelhos que ficam recebem a conta nova na cadeia antiga
    // (r/<aparelho>/<este>), cifrada com ECDH entre as chaves estáticas fixadas: o removido, que tem o código antigo,
    // não lê a mensagem dos outros. Mas consegue escrever uma sua: tem a chave fixada nos que ficam e, enquanto tiver
    // acesso ao transporte, apaga a verdadeira — por isso quem recebe só segue sozinho com um único remetente possível
    // (RekeyIsUnambiguous); senão pede o identificador do kit novo (SyncAgent.ConfirmRekeyAsync). Só recebem a conta nova os aparelhos com chave fixada ANTES (aqui não se fixa nenhuma chave
    // nova: um estado de aparelho escrito à pressa pelo removido não entra). Um aparelho sem chave fixada tem de voltar
    // a entrar (código de recuperação novo ou aprovação).
    //
    // Em dois passos, para resistir a falhas e a corridas: StartRotationAsync cria a conta nova sem tocar na antiga;
    // quem chama GUARDA o segredo novo, o estado novo e o RotationPending; só então FinishRotationAsync escreve as
    // mensagens r/ e apaga a cadeia antiga — o registo de chaves em último lugar, porque é a falta dele que diz aos
    // outros aparelhos para procurarem a mensagem. Uma falha a meio do 2.º passo continua na ronda seguinte.
    public async Task<(SyncEngine Engine, AccountSecret Secret)> RotateChainAsync(string? removed = null, CancellationToken cancellationToken = default)
    {
        var staying = State.PeerKeys.Where(p => p.Key != Device.Id && p.Key != removed).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var (engine, secret, pending) = await StartRotationAsync(removed, staying, cancellationToken).ConfigureAwait(false);
        await FinishRotationAsync(_transport, engine.State, secret, pending, cancellationToken).ConfigureAwait(false);
        return (engine, secret);
    }

    public async Task<(SyncEngine Engine, AccountSecret Secret, RotationPending Pending)> StartRotationAsync(string? removed,
        IReadOnlyDictionary<string, string> staying, CancellationToken cancellationToken = default)
    {
        await PushAsync(cancellationToken).ConfigureAwait(false);
        var keep = staying.Where(p => p.Key != Device.Id && p.Key != removed).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var secret = AccountSecret.Create();
        SyncEngine? engine = null;
        try
        {
            engine = await CreateAsync(_transport, secret, Device, _now, Options, cancellationToken).ConfigureAwait(false);
            engine.DeviceGroups = DeviceGroups;
            engine.State.DeviceKey = State.DeviceKey;
            foreach (var (peer, pub) in keep) engine.State.PeerKeys[peer] = pub;
            // Os IDs opacos dependem da DEK: os registos são reescritos com os IDs da cadeia nova, com a mesma versão.
            // As notas passam com o seu histórico (a base comum da fusão a três não se perde na troca de chave).
            foreach (var record in State.Records.Values)
            {
                // Os secretos voltam a ser selados com as chaves da cadeia nova (Key opaca e selo novos); os que não
                // abrem (adulterados) ficam para trás.
                var moved = Move(record, _keys.Seals, engine);
                if (moved is null) continue;
                engine.State.Records[SyncState.RecordKey(moved.Collection, moved.Id)] = moved;
                engine.State.Outbox[SyncState.RecordKey(moved.Collection, moved.Id)] = moved;
                engine._clock.Observe(moved.Version);
            }
            engine.State.NotesHistory = [.. State.NotesHistory];
            engine.State.OutboxNotes = [.. State.NotesHistory];
            engine.State.Clock = engine._clock.Last;
            engine.State.DeviceNames = new Dictionary<string, string>(State.DeviceNames, StringComparer.Ordinal);
            await engine.PushAsync(cancellationToken).ConfigureAwait(false);
            await engine.CompactAsync(cancellationToken).ConfigureAwait(false);
            await engine.WriteDeviceStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A conta antiga ficou intacta: a nova, a meio, sai (melhor esforço) para não ficar uma conta órfã no Drive.
            secret.Dispose();
            if (engine is not null)
            {
                try { await engine.DeleteEverythingAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException) { }
            }
            throw;
        }
        return (engine, secret, new RotationPending { OldChain = ChainId, Removed = removed, Staying = keep });
    }

    // 2.º passo da troca de chave (ver RotateChainAsync). Pode repetir-se: escrever uma mensagem outra vez é igual, e
    // apagar o que já não existe não faz nada. «state» é o estado da conta NOVA (o mesmo aparelho e a mesma chave estática).
    public static async Task FinishRotationAsync(ISyncTransport transport, SyncState state, AccountSecret secret, RotationPending pending,
        CancellationToken cancellationToken = default)
    {
        var old = pending.OldChain;
        var devices = pending.Staying.Keys.Append(state.Device.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        using (var mine = OpenDeviceKey(state))
        {
            foreach (var (peer, pub) in pending.Staying)
            {
                var key = RekeyKey(mine, pub, old, peer, state.Device.Id);
                var payload = new RekeyPayload
                {
                    Secret = Kdf.Base64Url(secret.ToBytes()), Chain = secret.ChainId, From = state.Device.Id, FromName = state.Device.Name,
                    Removed = pending.Removed, Devices = devices,
                };
                var context = SyncPaths.RekeyContext(peer, state.Device.Id);
                var file = SealedFile.Seal(key, SealedFile.Rekey, old, 1, context, Json.Bytes(payload));
                CryptographicOperations.ZeroMemory(key);
                await transport.WriteAsync(SyncPaths.Rekey(old, peer, state.Device.Id), file, cancellationToken).ConfigureAwait(false);
            }
        }
        var rekeys = SyncPaths.Chain(old) + "r/";
        foreach (var path in await transport.ListAsync(SyncPaths.Chain(old), cancellationToken).ConfigureAwait(false))
            if (path != SyncPaths.Keys(old) && !path.StartsWith(rekeys, StringComparison.Ordinal))
                await transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        await transport.DeleteAsync(SyncPaths.Keys(old), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] RekeyKey(ECDiffieHellman mine, string otherPublic, string chain, string to, string from)
    {
        using var other = PairingProtocol.ImportPublic(Kdf.FromBase64Url(otherPublic));
        var shared = mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
        try { return Kdf.Derive(shared, $"eteris-rekey-v1|{chain}|{to}|{from}"); }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    // A conta deste aparelho deixou de existir (ChainGoneException): outro aparelho fez «nova cadeia» e deixou-lhe a
    // conta nova? Lê TODAS as mensagens r/<este>/ da cadeia «chain» (por omissão a do estado) e só abre as de aparelhos
    // com chave fixada. Um remetente que outra mensagem válida dá como removido é recusado (o removido também tinha a
    // chave fixada e podia deixar uma «conta nova» dele); remetentes que se contradizem, ou contas novas diferentes,
    // dão Conflict (pergunta-se ao utilizador). Nada sai do transporte aqui: quem chama apaga as mensagens (Paths) só
    // depois de guardar a conta nova. Devolve null quando não há mensagem nenhuma (removido, ou dados apagados).
    public static async Task<RekeyResult?> ReceiveRekeyAsync(ISyncTransport transport, SyncState state, string? chain = null,
        CancellationToken cancellationToken = default)
    {
        if (state.DeviceKey is null) return null;
        chain ??= state.ChainId;
        using var mine = OpenDeviceKey(state);
        var folder = SyncPaths.Rekeys(chain, state.Device.Id);
        var found = new List<(string Path, RekeyPayload Payload, AccountSecret Secret)>();
        try
        {
            foreach (var path in await transport.ListAsync(folder, cancellationToken).ConfigureAwait(false))
            {
                var from = path[folder.Length..];
                if (!state.PeerKeys.TryGetValue(from, out var pub)) continue;
                try
                {
                    var bytes = await transport.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                    if (bytes is null) continue;
                    var key = RekeyKey(mine, pub, chain, state.Device.Id, from);
                    RekeyPayload payload;
                    try { payload = Json.Parse<RekeyPayload>(SealedFile.Open(key, SealedFile.Rekey, chain, SyncPaths.RekeyContext(state.Device.Id, from), bytes)); }
                    finally { CryptographicOperations.ZeroMemory(key); }
                    if (payload.From != from || payload.Removed == state.Device.Id) continue;
                    var secret = AccountSecret.FromBytes(Kdf.FromBase64Url(payload.Secret));
                    if (secret.ChainId != payload.Chain)
                    {
                        secret.Dispose();
                        continue;
                    }
                    found.Add((path, payload, secret));
                }
                catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException or UnsupportedFormatException or PairingException) { }
            }
            if (found.Count == 0) return null;
            var accused = found.Select(f => f.Payload.Removed).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var trusted = found.Where(f => !accused.Contains(f.Payload.From)).ToList();
            if (trusted.Count == 0 || trusted.Select(f => f.Payload.Chain).Distinct(StringComparer.Ordinal).Count() > 1)
                return new RekeyResult(null, string.Empty, string.Empty, null, [], Conflict: true);
            var chosen = trusted[0];
            found.Remove(chosen);
            return new RekeyResult(chosen.Secret, chosen.Payload.From, chosen.Payload.FromName, chosen.Payload.Devices,
                trusted.Select(t => t.Path).ToList(), Conflict: false);
        }
        finally
        {
            foreach (var (_, _, secret) in found) secret.Dispose();
        }
    }

    // Uma mensagem r/ de «from» só pode ser de quem trocou a chave quando «from» é a única chave fixada neste aparelho.
    // Com outra chave fixada, a mensagem pode ser de um aparelho removido: tem a chave dele fixada aqui e, enquanto
    // tiver acesso ao transporte, consegue apagar a mensagem verdadeira (com a acusação) e deixar a sua.
    public static bool RekeyIsUnambiguous(SyncState state, string from)
        => state.PeerKeys.Keys.All(id => id == state.Device.Id || id == from);

    // As mensagens r/ já tratadas (a conta nova ficou guardada): saem do transporte.
    public static async Task ForgetRekeysAsync(ISyncTransport transport, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths) await transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // Apaga tudo o que esta conta tem no transporte (Definições › Sincronização › «Apagar os dados na nuvem»).
    public async Task DeleteEverythingAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in await _transport.ListAsync(SyncPaths.Chain(ChainId), cancellationToken).ConfigureAwait(false))
            await _transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // Impressão digital do estado sincronizado (para comparar aparelhos nos testes e no diagnóstico).
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var record in State.Records.Values.Where(r => r.Collection != Collections.Notes).OrderBy(r => r.Collection, StringComparer.Ordinal)
                     .ThenBy(r => r.Id, StringComparer.Ordinal))
            sb.Append(record.Canonical()).Append('\n');
        sb.Append("notes:").Append(Notes);
        return Kdf.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())).AsSpan(0, 8));
    }
}
