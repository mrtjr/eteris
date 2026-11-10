using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Eteris.Sync.Model;

// Coleções que a sincronização conhece e como cada uma se funde (docs/sincronizacao.md, «Modelo»).
public static class Collections
{
    public const string Bookmarks = "bookmarks"; // chave: URL normalizado; LWW por favorito
    public const string Spaces = "spaces";       // chave: ID do Espaço; LWW (nome, cor, ícone, ordem)
    public const string Tabs = "tabs";           // chave: ID do aparelho; instantâneo das abas abertas desse aparelho
    public const string Settings = "settings";   // chave: nome da definição (só as de SyncableSettings); LWW
    public const string Shield = "shield";       // chave: «tipo|site|valor»; conjunto com lápides (LWW por elemento)
    public const string Notes = "notes";         // chave: «notes»; fusão 3-way por linhas (NotesMerge)

    // Coleções secretas (cofre de palavras-passe e preenchimento automático, docs/sincronizacao.md «Coleções secretas»):
    // cada registo leva um selo próprio por dentro da cifra do ficheiro (Secrets/SecretSeal), com a chave natural e os
    // campos lá dentro; a Key no motor é opaca («s:» + HMAC). Os campos estão em Secrets/SecretSchemas.
    public const string Logins = "logins";           // palavras-passe; chave natural: origem e utilizador
    public const string LoginsNever = "loginsNever"; // «Nunca neste site»; chave natural: origem
    public const string Cards = "cards";             // cartões (nunca o código de segurança); chave natural: ID do item
    public const string Ibans = "ibans";
    public const string Contacts = "contacts";       // informações de contacto (moradas, e-mails, telefones)
    public const string IdDocs = "idDocs";           // documentos de identificação (passaporte, carta de condução/CNH, CC/RG, NIF/CPF)
    public const string Travel = "travel";           // viagens (veículos, programas de fidelidade)

    public static readonly IReadOnlyList<string> Secret = [Logins, LoginsNever, Cards, Ibans, Contacts, IdDocs, Travel];

    public static bool IsSecret(string collection) => Secret.Contains(collection, StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> All = [Bookmarks, Spaces, Tabs, Settings, Shield, Notes, .. Secret];

    // Ficheiros com abas abertas não são comprimidos (SealedFile: canal lateral de compressão).
    public static bool HasTabs(IEnumerable<SyncRecord> records) => records.Any(r => r.Collection == Tabs);

    public const string NotesKey = "notes";

    // Escolha da conta (0.2.15): a conta sincroniza pagamentos e documentos. Registo de «settings» com chave reservada,
    // fora de SyncableSettings: nenhum perfil o aplica como definição. Campos: «value» ("on"/"off"; só "on" conta como
    // ligado), «since» (ms) e «by» (id do aparelho que o escreveu), estes dois só informativos.
    public const string AccountSecretsKey = "@account/secret-groups";

    // Chaves de «settings» reservadas ao Éteris (começam por «@»): nunca são definições de um perfil.
    public static bool IsReservedSetting(string key) => key.StartsWith('@');

    // Definições que viajam entre aparelhos. Fora da lista ficam as que são deste computador (janela, GPU, sandbox,
    // pasta de transferências, barra lateral aberta…) e as de privacidade que dependem da rede local.
    public static readonly IReadOnlySet<string> SyncableSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        "engine", "startup", "sleep", "archive", "httpsFirst", "cookies", "autoplay", "suggest", "dns", "clearOnExit",
        "theme", "cleanUrls", "debounce", "bounceForget", "cname", "notifications", "siteLanguage", "verticalTabs",
        "density", "accent", "ntTheme", "ntWallpaper", "ntWallpaperMotion", "ntClock", "ntTiles", "ntWeek", "ntTips",
        "readerTheme", "readerSize", "readerSerif",
        // Palavras-passe e preenchimento automático: oferecer para guardar e sugestões (os «Pedir o Windows Hello…» são
        // de cada computador e não viajam).
        "pwOffer", "pwSuggest", "afPayments", "afContacts", "afDocuments", "afTravel",
    };

    // Tipos de decisão do Escudo que se sincronizam (as listas de filtros, cada aparelho descarrega as suas).
    public static readonly IReadOnlySet<string> ShieldKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "off",      // Escudo desligado num site
        "strict",   // modo estrito num site
        "hide",     // elemento escondido (site|seletor)
        "rule",     // regra por site (site|domínio bloqueado)
        "http",     // exceção ao HTTPS primeiro
    };
}

// Um registo sincronizado. Tudo isto viaja só dentro dos lotes cifrados: nem o ID opaco se vê de fora.
//   Id      — opaco e estável (KeyRing.OpaqueId da coleção e da chave natural);
//   Key     — a chave natural (URL, nome da definição…), para o aparelho aplicar o registo;
//   Version — HLC da última alteração (quem ganha no LWW);
//   Deleted — lápide: o registo foi apagado (fica até ao prazo, SyncOptions.TombstoneTtl);
//   Data    — campos do registo; os desconhecidos (de um Éteris mais recente) são preservados tal e qual;
//   Schema  — versão do esquema deste registo.
public sealed record SyncRecord
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("c")] public required string Collection { get; init; }
    [JsonPropertyName("i")] public required string Id { get; init; }
    [JsonPropertyName("k")] public required string Key { get; init; }
    [JsonPropertyName("v")] [JsonConverter(typeof(HlcJsonConverter))] public required Hlc Version { get; init; }
    [JsonPropertyName("x")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Deleted { get; init; }
    [JsonPropertyName("d")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonObject? Data { get; init; }
    [JsonPropertyName("s")] public int Schema { get; init; } = CurrentSchema;

    // Campos que um Éteris mais recente acrescentou ao registo: voltam a ser escritos como vieram.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    public string? Text(string field) => Data?[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Impressão digital estável (ordem dos campos normalizada): para comparar réplicas nos testes e no diagnóstico.
    public string Canonical()
    {
        var sb = new StringBuilder();
        sb.Append(Collection).Append('|').Append(Id).Append('|').Append(Key).Append('|').Append(Version).Append('|').Append(Deleted ? 1 : 0).Append('|');
        if (!Deleted && Data is not null) CanonicalJson.Write(Data, sb);
        return sb.ToString();
    }
}

public sealed class HlcJsonConverter : JsonConverter<Hlc>
{
    public override Hlc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Hlc.Parse(reader.GetString() ?? throw new JsonException("HLC em falta"));

    public override void Write(Utf8JsonWriter writer, Hlc value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public static class CanonicalJson
{
    public static string Of(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    public static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (name, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(name)).Append(':');
                    Write(value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(array[i], sb);
                }
                sb.Append(']');
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }
}
