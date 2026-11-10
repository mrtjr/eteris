using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;

namespace Eteris.Sync.Engine;

public sealed record DeviceIdentity(string Id, string Name, string Kind = "pc")
{
    public static DeviceIdentity Create(string name, string kind = "pc") => new(Kdf.Hex(RandomNumberGenerator.GetBytes(16)), name, kind);
}

public sealed record SyncOptions
{
    // Prazo das lápides: um apagado é lembrado durante este tempo. Um aparelho ausente há mais tempo recomeça do
    // instantâneo antes de enviar o que quer que seja (não ressuscita o que os outros apagaram).
    public TimeSpan TombstoneTtl { get; init; } = TimeSpan.FromDays(90);

    // Versões das notas guardadas para achar a base comum da fusão.
    public int NotesHistoryLimit { get; init; } = 64;
}

// Lote já preparado mas ainda não confirmado pelo transporte: volta a ser enviado IGUAL (mesmo número, mesmo conteúdo),
// para nunca haver dois lotes diferentes com o mesmo número.
public sealed record PendingBatch(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("records")] List<SyncRecord> Records,
    [property: JsonPropertyName("notes")] List<NotesVersion> Notes,
    [property: JsonPropertyName("created")] long CreatedMs);

// Uma troca de chave («nova cadeia») a meio: a conta nova já existe e está guardada neste aparelho, mas a antiga ainda
// tem de receber as mensagens para os aparelhos que ficam e ser apagada. Guardada até acabar (SyncEngine.FinishRotationAsync),
// para uma falha a meio continuar na ronda seguinte em vez de deixar os outros aparelhos sem a conta nova.
public sealed record RotationPending
{
    [JsonPropertyName("old")] public required string OldChain { get; init; }
    [JsonPropertyName("removed")] public string? Removed { get; init; }
    // Aparelhos que recebem a conta nova → a chave pública fixada de cada um (só os que o utilizador viu na lista).
    [JsonPropertyName("staying")] public required Dictionary<string, string> Staying { get; init; }
}

// Estado local de um aparelho (o anfitrião guarda-o protegido: DPAPI no Windows). Não vai para o transporte.
public sealed class SyncState
{
    [JsonPropertyName("device")] public required DeviceIdentity Device { get; set; }
    [JsonPropertyName("chain")] public required string ChainId { get; set; }
    [JsonPropertyName("next")] public long NextSeq { get; set; } = 1;
    [JsonPropertyName("read")] public Dictionary<string, long> Read { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("records")] public Dictionary<string, SyncRecord> Records { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("outbox")] public Dictionary<string, SyncRecord> Outbox { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("outNotes")] public List<NotesVersion> OutboxNotes { get; set; } = [];
    [JsonPropertyName("pending")] public PendingBatch? Pending { get; set; }
    [JsonPropertyName("notes")] public List<NotesVersion> NotesHistory { get; set; } = [];
    [JsonPropertyName("names")] public Dictionary<string, string> DeviceNames { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("clock")] [JsonConverter(typeof(HlcJsonConverter))] public Hlc Clock { get; set; } = Hlc.Zero;
    [JsonPropertyName("lastPull")] public long LastPullMs { get; set; }
    [JsonPropertyName("ownBatches")] public Dictionary<long, long> OwnBatchTimes { get; set; } = [];
    // Em que lote deste aparelho seguiu a última versão enviada de cada registo (até o instantâneo próprio a cobrir).
    [JsonPropertyName("sent")] public Dictionary<string, long> SentIn { get; set; } = new(StringComparer.Ordinal);
    // Chave estática P-256 deste aparelho (PKCS#8, base64url): só serve para receber a conta nova quando outro aparelho
    // faz «nova cadeia» ao remover um terceiro (SyncEngine.RotateChainAsync). A pública vai no estado do aparelho.
    [JsonPropertyName("dkey")] public string? DeviceKey { get; set; }
    // Chaves públicas dos outros aparelhos, fixadas na primeira vez que se veem (aparelho → SPKI em base64url).
    [JsonPropertyName("peers")] public Dictionary<string, string> PeerKeys { get; set; } = new(StringComparer.Ordinal);
    // As chaves dos selos da cadeia atual (Secrets/SealKeys): só para voltar a selar os registos secretos deste aparelho
    // depois de outro aparelho fazer «nova cadeia» (a cadeia antiga já foi apagada quando este aparelho dá por isso).
    [JsonPropertyName("seal")] public SealKeys? Seals { get; set; }

    public static string RecordKey(string collection, string id) => collection + "/" + id;

    public byte[] ToBytes() => Json.Bytes(this);

    public static SyncState Parse(ReadOnlySpan<byte> json) => Json.Parse<SyncState>(json);
}
