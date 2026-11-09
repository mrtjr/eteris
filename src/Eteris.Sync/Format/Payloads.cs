using System.Text.Json;
using System.Text.Json.Serialization;
using Eteris.Sync.Model;

namespace Eteris.Sync.Format;

// Conteúdo (JSON, sempre dentro de um SealedFile) dos ficheiros da sincronização.

// Lote imutável: alterações de um aparelho, numeradas 1, 2, 3… Nunca é reescrito; só apagado quando todos os
// aparelhos o leram e um instantâneo o cobre.
public sealed record BatchPayload
{
    [JsonPropertyName("format")] public int Format { get; init; } = SealedFile.FormatVersion;
    [JsonPropertyName("device")] public required string Device { get; init; }
    [JsonPropertyName("name")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("seq")] public required long Seq { get; init; }
    [JsonPropertyName("created")] public long CreatedMs { get; init; }
    [JsonPropertyName("records")] public required IReadOnlyList<SyncRecord> Records { get; init; }
    [JsonPropertyName("notes")] public IReadOnlyList<NotesVersion>? NotesHistory { get; init; }
}

// Instantâneo (compactação): o estado inteiro, mais o que cobre ({aparelho: último lote}) e o horizonte das lápides
// (as mais antigas do que isto já saíram: um aparelho ausente há mais tempo recomeça daqui).
public sealed record SnapshotPayload
{
    [JsonPropertyName("format")] public int Format { get; init; } = SealedFile.FormatVersion;
    [JsonPropertyName("device")] public required string Device { get; init; }
    [JsonPropertyName("seq")] public required long Seq { get; init; }
    [JsonPropertyName("created")] public long CreatedMs { get; init; }
    [JsonPropertyName("covers")] public required Dictionary<string, long> Covers { get; init; }
    [JsonPropertyName("horizon")] public long TombstoneHorizonMs { get; init; }
    [JsonPropertyName("records")] public required IReadOnlyList<SyncRecord> Records { get; init; }
    [JsonPropertyName("notes")] public IReadOnlyList<NotesVersion>? NotesHistory { get; init; }
}

// Estado de um aparelho (único ficheiro que se reescreve, e só pelo próprio): nome e tipo (só aqui, cifrados), o
// que já leu de cada aparelho e quando sincronizou. Serve para saber que lotes já se podem apagar.
public sealed record DeviceStatePayload
{
    [JsonPropertyName("device")] public required string Device { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; init; } = "pc";
    [JsonPropertyName("read")] public required Dictionary<string, long> Read { get; init; }
    [JsonPropertyName("last")] public long LastSyncMs { get; init; }
    // Chave pública estática do aparelho (SPKI P-256, base64url): os outros fixam-na na primeira vez que a veem.
    [JsonPropertyName("pub")] public string? PublicKey { get; init; }
}

// Conta nova entregue a um aparelho que fica, quando outro faz «nova cadeia» para tirar um terceiro da conta.
// Cifrada com ECDH entre as chaves estáticas dos dois aparelhos (fixadas antes): o aparelho removido, que tem o código
// antigo, não a abre nem a forja.
public sealed record RekeyPayload
{
    [JsonPropertyName("secret")] public required string Secret { get; init; }
    [JsonPropertyName("chain")] public required string Chain { get; init; }
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("name")] public string FromName { get; init; } = string.Empty;
}

// Uma versão das notas: o texto e de que versões descende (hashes), para achar a base comum da fusão a três.
public sealed record NotesVersion(
    [property: JsonPropertyName("h")] string Hash,
    [property: JsonPropertyName("p")] IReadOnlyList<string> Parents,
    [property: JsonPropertyName("v")] [property: JsonConverter(typeof(HlcJsonConverter))] Hlc Version,
    [property: JsonPropertyName("t")] string Text);

// Perfil exportado para um ficheiro (Definições › Sincronização › Exportar perfil cifrado).
public sealed record ArchivePayload
{
    [JsonPropertyName("format")] public int Format { get; init; } = SealedFile.FormatVersion;
    [JsonPropertyName("created")] public long CreatedMs { get; init; }
    [JsonPropertyName("device")] public string DeviceName { get; init; } = string.Empty;
    [JsonPropertyName("records")] public required IReadOnlyList<SyncRecord> Records { get; init; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Parse<T>(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options) ?? throw new FormatException("Conteúdo vazio."); }
        catch (JsonException e) { throw new FormatException("Conteúdo ilegível: " + e.Message, e); }
    }
}
