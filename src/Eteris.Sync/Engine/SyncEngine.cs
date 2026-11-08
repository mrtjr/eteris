using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Merge;
using Eteris.Sync.Model;
using Eteris.Sync.Transport;

namespace Eteris.Sync.Engine;

// A conta deixou de existir neste transporte (outro aparelho fez «nova cadeia» ou a pasta foi apagada).
public sealed class ChainGoneException(string message) : Exception(message);

public sealed record PullResult(int Applied, IReadOnlyDictionary<string, string> Pending, bool NeedsUpdate, bool Rebased);

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
    }

    public SyncState State { get; }

    public SyncOptions Options { get; }

    public DeviceIdentity Device => State.Device;

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
        return new SyncEngine(transport, keys, state, now, options);
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
        var version = _clock.Now();
        Accept(new SyncRecord
        {
            Collection = collection,
            Id = _keys.OpaqueId(collection, key),
            Key = key,
            Version = version,
            Deleted = deleted,
            Data = deleted ? null : (JsonObject)data!.DeepClone(),
        }, outgoing: true);
        return version;
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
        var file = SealedFile.Seal(_keys.FileKey(SealedFile.Batch, context), SealedFile.Batch, ChainId, _keys.Generation, context, Json.Bytes(payload));
        await _transport.WriteAsync(SyncPaths.Batch(ChainId, Device.Id, batch.Seq), file, cancellationToken).ConfigureAwait(false);
        State.OwnBatchTimes[batch.Seq] = batch.CreatedMs;
        State.Pending = null;
    }

    // ---- receção --------------------------------------------------------------------------------------------------

    public async Task<PullResult> PullAsync(CancellationToken cancellationToken = default)
    {
        if (await _transport.ReadAsync(SyncPaths.Keys(ChainId), cancellationToken).ConfigureAwait(false) is null)
            throw new ChainGoneException("Esta conta deixou de existir: outro aparelho criou uma nova cadeia. Peça o código novo.");

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
        var needsUpdate = false;
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
            if (error is UnsupportedFormatException) needsUpdate = true;
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
                catch (UnsupportedFormatException e)
                {
                    pending[device] = e.Message;
                    needsUpdate = true;
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

    // Recomeça dos instantâneos: o estado sincronizado passa a ser a junção deles (cada um vale até ao que cobre) e só o
    // que ainda não foi enviado daqui fica por cima. É o que impede um aparelho ausente há mais do que o prazo das
    // lápides de ressuscitar apagados: um registo vivo num instantâneo velho que falta num mais recente, e que é mais
    // antigo do que o horizonte das lápides deste, foi apagado e a lápide já saiu — não volta.
    private void Rebase(List<SnapshotPayload> snapshots)
    {
        var unsent = State.Outbox.Values.Concat(State.Pending?.Records ?? []).ToList();
        State.Records = new Dictionary<string, SyncRecord>(StringComparer.Ordinal);
        State.Outbox = new Dictionary<string, SyncRecord>(StringComparer.Ordinal);
        State.Read = new Dictionary<string, long>(StringComparer.Ordinal);
        var ordered = snapshots.OrderBy(s => s.CreatedMs).ToList();
        foreach (var snapshot in ordered)
        {
            foreach (var version in snapshot.NotesHistory ?? []) Remember(version, outgoing: false);
            foreach (var record in snapshot.Records)
            {
                var purged = ordered.Any(newer => newer.CreatedMs > snapshot.CreatedMs && record.Version.Wall < newer.TombstoneHorizonMs
                                                  && newer.Records.All(r => r.Id != record.Id || r.Collection != record.Collection));
                if (!purged) Merge(record);
            }
            foreach (var (device, seq) in snapshot.Covers)
                if (device != Device.Id) State.Read[device] = Math.Max(State.Read.GetValueOrDefault(device), seq);
        }
        foreach (var record in unsent) Merge(record, local: true);
    }

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

    private string Label(string node) => State.DeviceNames.TryGetValue(node, out var name) && name.Length > 0 ? name : "aparelho " + node[..Math.Min(6, node.Length)];

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
                if (state.Device == device) list.Add(state);
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
        var file = SealedFile.Seal(_keys.FileKey(SealedFile.Snapshot, context), SealedFile.Snapshot, ChainId, _keys.Generation, context, Json.Bytes(snapshot));
        await _transport.WriteAsync(SyncPaths.Snapshot(ChainId, Device.Id, mine), file, cancellationToken).ConfigureAwait(false);

        // Instantâneos antigos deste aparelho saem.
        foreach (var path in await _transport.ListAsync(SyncPaths.Snapshots(ChainId), cancellationToken).ConfigureAwait(false))
            if (SyncPaths.TryParseSnapshot(ChainId, path, out var device, out var seq) && device == Device.Id && seq < mine)
                await _transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);

        // Lotes que já ninguém precisa: lidos por todos os outros aparelhos conhecidos, ou mais velhos do que o prazo.
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

    // «Nova cadeia» (aparelho perdido, código exposto): segredo novo, DEK nova, o estado todo recifrado na pasta da
    // cadeia nova e a antiga apagada. Os outros aparelhos deixam de conseguir sincronizar até receberem o código novo.
    public async Task<(SyncEngine Engine, AccountSecret Secret)> RotateChainAsync(CancellationToken cancellationToken = default)
    {
        await PushAsync(cancellationToken).ConfigureAwait(false);
        var secret = AccountSecret.Create();
        var device = Device;
        var engine = await CreateAsync(_transport, secret, device, _now, Options, cancellationToken).ConfigureAwait(false);
        // Os IDs opacos dependem da DEK: os registos são reescritos com os IDs da cadeia nova.
        foreach (var record in State.Records.Values.Where(r => r.Collection != Collections.Notes))
        {
            var moved = record with { Id = engine._keys.OpaqueId(record.Collection, record.Key) };
            engine.State.Records[SyncState.RecordKey(moved.Collection, moved.Id)] = moved;
            engine.State.Outbox[SyncState.RecordKey(moved.Collection, moved.Id)] = moved;
            engine._clock.Observe(moved.Version);
        }
        engine.State.DeviceNames = new Dictionary<string, string>(State.DeviceNames, StringComparer.Ordinal);
        if (Get(Collections.Notes, Collections.NotesKey) is not null) engine.SetNotes(Notes);
        await engine.PushAsync(cancellationToken).ConfigureAwait(false);
        await engine.CompactAsync(cancellationToken).ConfigureAwait(false);

        foreach (var path in await _transport.ListAsync(SyncPaths.Chain(ChainId), cancellationToken).ConfigureAwait(false))
            if (path != SyncPaths.Keys(ChainId)) await _transport.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        await _transport.DeleteAsync(SyncPaths.Keys(ChainId), cancellationToken).ConfigureAwait(false);
        return (engine, secret);
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
