using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Model;

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

// Estado local de um aparelho (o anfitrião guarda-o protegido: DPAPI no Windows). Não vai para o transporte.
public sealed class SyncState
{
    [JsonPropertyName("device")] public required DeviceIdentity Device { get; init; }
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

    public static string RecordKey(string collection, string id) => collection + "/" + id;

    public byte[] ToBytes() => Json.Bytes(this);

    public static SyncState Parse(ReadOnlySpan<byte> json) => Json.Parse<SyncState>(json);
}
