using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;

namespace Eteris.Browser.Vault;

// Um item do cofre no formato da sincronização e de volta (D3). Os nomes dos campos são os de Eteris.Sync.Secrets.
// SecretSchemas (desenho, secção 4.1); os derivados (marca e últimos 4 algarismos do cartão, máscaras) não viajam e
// refazem-se ao gravar. Ida e volta exata: o que vem de outro aparelho grava-se campo a campo tal como veio (o cofre só
// confere), por isso a impressão deste computador sai igual à do conteúdo recebido e nada volta para trás em eco.
public static partial class VaultSyncMap
{
    // Chave natural de uma palavra-passe gerada ainda por confirmar (C5): ela pode ter o mesmo site e utilizador que uma
    // confirmada, por isso é o ID do item.
    public const string GeneratedPrefix = "gerada\n";

    public static string LoginKey(string origin, string username) => origin + "\n" + username;

    private static readonly string[] LoginMeta = ["origin", "username", "created", "changed", "used", "formAction", "generated", "realm"];
    private static readonly string[] LoginSecret = ["password", "note"];
    private static readonly string[] CardMeta = ["name", "expMonth", "expYear", "nickname", "created", "used"];
    private static readonly string[] IdDocMeta = ["type", "name", "country", "issuer", "issued", "expires"];
    private static readonly string[] TravelMeta = ["type", "make", "model", "year", "owner", "plate", "plateRegion", "company", "program", "name"];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ItemId();

    public static bool IsItemId(string id) => ItemId().IsMatch(id);

    // O item no formato da sincronização: a chave natural e os campos (null nos que estão vazios).
    public static SecretItem ToSync(VaultCollection collection, string id, JsonObject meta, JsonObject? secret)
    {
        secret ??= new JsonObject();
        var name = VaultCollections.Name(collection);
        var data = new JsonObject();
        foreach (var field in SecretSchemas.Fields[name].Keys)
            data[field] = (meta[field] ?? (secret.ContainsKey(field) ? secret[field] : null))?.DeepClone();
        switch (collection)
        {
            case VaultCollection.Logins:
                // «number», «note»… não existem nos metadados das palavras-passe; a palavra-passe e a nota vêm do segredo.
                foreach (var field in LoginSecret) data[field] = secret[field]?.DeepClone();
                var generated = meta["generated"] is JsonValue g && g.TryGetValue<bool>(out var b) && b;
                return new SecretItem(generated ? GeneratedPrefix + id : LoginKey(Text(meta, "origin") ?? string.Empty, Text(meta, "username") ?? string.Empty), data);
            case VaultCollection.LoginsNever:
                return new SecretItem(Text(meta, "origin") ?? string.Empty, data);
            case VaultCollection.Cards:
                data["number"] = secret["number"]?.DeepClone();
                return new SecretItem(id, data);
            case VaultCollection.Ibans:
                data["iban"] = secret["iban"]?.DeepClone();
                return new SecretItem(id, data);
            case VaultCollection.IdDocs:
                data["number"] = secret["number"]?.DeepClone();
                data["note"] = secret["note"]?.DeepClone();
                return new SecretItem(id, data);
            case VaultCollection.Travel:
                data["vin"] = secret["vin"]?.DeepClone();
                data["number"] = secret["number"]?.DeepClone();
                return new SecretItem(id, data);
            default:
                return new SecretItem(id, data);
        }
    }

    // Como gravar no cofre um item vindo de outro aparelho: o ID local (null = novo; ou o da chave natural), a origem
    // para os índices e o que vai nos metadados e no segredo. null = não é aceitável (campos de tipo errado, código de
    // segurança, origem que não é uma origem normalizada, cartão ou IBAN inválido, chave natural que não bate com os
    // campos): fica de fora.
    public sealed record Stored(string? FixedId, string? IndexOrigin, JsonObject Meta, JsonObject? Secret);

    public static Stored? FromSync(VaultCollection collection, SecretItem item)
    {
        var name = VaultCollections.Name(collection);
        var d = item.Data;
        if (!SecretSchemas.IsValid(name, d)) return null;
        try { SecretSchemas.RejectForbidden(d); }
        catch (ArgumentException) { return null; }
        JsonNode? F(string field) => d[field]?.DeepClone();
        JsonObject Pick(IEnumerable<string> fields)
        {
            var o = new JsonObject();
            foreach (var field in fields) o[field] = F(field);
            return o;
        }
        switch (collection)
        {
            case VaultCollection.Logins:
            {
                if (Text(d, "origin") is not { } origin || Origins.Normalize(origin) != origin) return null;
                var generated = d["generated"] is JsonValue g && g.TryGetValue<bool>(out var b) && b;
                string? fixedId = null;
                if (generated)
                {
                    if (!item.NaturalKey.StartsWith(GeneratedPrefix, StringComparison.Ordinal) || !IsItemId(item.NaturalKey[GeneratedPrefix.Length..])) return null;
                    fixedId = item.NaturalKey[GeneratedPrefix.Length..];
                }
                else if (item.NaturalKey != LoginKey(origin, Text(d, "username") ?? string.Empty)) return null;
                return new Stored(fixedId, Origins.IsAndroid(origin) ? null : origin, Pick(LoginMeta), Pick(LoginSecret));
            }
            case VaultCollection.LoginsNever:
            {
                if (Text(d, "origin") is not { } origin || Origins.Normalize(origin) != origin || item.NaturalKey != origin) return null;
                return new Stored(null, origin, Pick(["origin", "created"]), null);
            }
            case VaultCollection.Cards:
            {
                if (!IsItemId(item.NaturalKey) || Text(d, "number") is not { } number || number != VaultFields.Digits(number) || !CardRules.IsValidNumber(number))
                    return null;
                var meta = Pick(CardMeta);
                meta["brand"] = CardRules.Brand(number);
                meta["last4"] = number[^4..];
                return new Stored(item.NaturalKey, null, meta, new JsonObject { ["number"] = number });
            }
            case VaultCollection.Ibans:
            {
                if (!IsItemId(item.NaturalKey) || Text(d, "iban") is not { } iban || !IbanRules.IsValid(iban)) return null;
                var meta = Pick(["nickname", "created"]);
                meta["masked"] = IbanRules.Mask(iban);
                return new Stored(item.NaturalKey, null, meta, new JsonObject { ["iban"] = iban });
            }
            case VaultCollection.Contacts:
                if (!IsItemId(item.NaturalKey)) return null;
                return new Stored(item.NaturalKey, null, Pick(SecretSchemas.Fields[name].Keys), null);
            case VaultCollection.IdDocs:
            {
                if (!IsItemId(item.NaturalKey) || IdDocItem.ParseType(Text(d, "type")) is null || Text(d, "number") is not { Length: > 0 } number) return null;
                var meta = Pick(IdDocMeta);
                meta["masked"] = VaultFields.MaskTail(number);
                return new Stored(item.NaturalKey, null, meta, new JsonObject { ["number"] = number, ["note"] = F("note") });
            }
            case VaultCollection.Travel:
            {
                if (!IsItemId(item.NaturalKey) || Text(d, "type") is not ("vehicle" or "loyalty")) return null;
                var meta = Pick(TravelMeta);
                meta["masked"] = string.IsNullOrWhiteSpace(Text(d, "number")) ? null : VaultFields.MaskTail(Text(d, "number"));
                return new Stored(item.NaturalKey, null, meta, new JsonObject { ["vin"] = F("vin"), ["number"] = F("number") });
            }
            default:
                return null;
        }
    }

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
