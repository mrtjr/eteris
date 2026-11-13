using System.Text.Json.Nodes;
using Eteris.Sync.Model;

namespace Eteris.Sync.Secrets;

public enum SecretFieldType { Text, Integer, Boolean }

// Os campos de cada coleção secreta (desenho do gestor de palavras-passe, secção 4.1; docs/sincronizacao.md, «Coleções
// secretas»). Ficam aqui, na biblioteca partilhada, para o Android usar os mesmos nomes. Tudo isto viaja só DENTRO do
// selo (SecretSeal): fora dele não há nenhum destes campos, nem o site.
//
// O código de segurança do cartão (CVV/CVC/CSC/CID) nunca entra: um selo com um campo desses é recusado.
public static class SecretSchemas
{
    private static IReadOnlyDictionary<string, SecretFieldType> Of(params (string Name, SecretFieldType Type)[] fields)
    {
        var map = new Dictionary<string, SecretFieldType>(StringComparer.Ordinal);
        foreach (var (name, type) in fields) map.Add(name, type);
        return map;
    }

    private const SecretFieldType T = SecretFieldType.Text;
    private const SecretFieldType I = SecretFieldType.Integer;
    private const SecretFieldType B = SecretFieldType.Boolean;

    // A «última utilização» viaja dentro do selo mas não entra na impressão: preencher um item não conta como alterá-lo
    // (senão um computador que ainda não recebeu a palavra-passe nova repunha a antiga em todos). Segue com a alteração
    // seguinte do item.
    public const string LastUsedField = "used";

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, SecretFieldType>> Fields =
        new Dictionary<string, IReadOnlyDictionary<string, SecretFieldType>>(StringComparer.Ordinal)
        {
            // chave natural: origem \n utilizador (uma gerada ainda por confirmar: «gerada» \n ID)
            [Collections.Logins] = Of(("origin", T), ("username", T), ("password", T), ("note", T), ("created", T), ("changed", T), ("used", T),
                ("formAction", T), ("generated", B), ("realm", T)),
            // chave natural: a origem
            [Collections.LoginsNever] = Of(("origin", T), ("created", T)),
            // chave natural das restantes: o ID aleatório do item (criado uma vez, igual em todos os aparelhos)
            [Collections.Cards] = Of(("name", T), ("number", T), ("expMonth", I), ("expYear", I), ("nickname", T), ("created", T), ("used", T)),
            [Collections.Ibans] = Of(("iban", T), ("nickname", T), ("created", T)),
            [Collections.Contacts] = Of(("label", T), ("givenName", T), ("familyName", T), ("organization", T), ("street", T), ("number", T),
                ("complement", T), ("neighborhood", T), ("city", T), ("region", T), ("postalCode", T), ("country", T), ("email", T), ("phone", T),
                ("created", T), ("used", T)),
            // type: passport, driverLicense, nationalId, taxId (NIF/CPF)
            [Collections.IdDocs] = Of(("type", T), ("number", T), ("name", T), ("country", T), ("issuer", T), ("issued", T), ("expires", T), ("note", T)),
            // type: vehicle (marca, modelo, ano, proprietário, matrícula/placa, região, VIN) ou loyalty (companhia, programa, número, nome)
            [Collections.Travel] = Of(("type", T), ("make", T), ("model", T), ("year", I), ("owner", T), ("plate", T), ("plateRegion", T), ("vin", T),
                ("company", T), ("program", T), ("number", T), ("name", T)),
        };

    private static readonly HashSet<string> ForbiddenExact = new(StringComparer.Ordinal)
    {
        "cid", "cvn", "securitycode", "cardsecuritycode", "cardverificationcode", "cardverificationvalue",
    };

    // O nome é de um código de segurança do cartão? (Sem distinguir maiúsculas, hífenes nem sublinhados.)
    public static bool IsForbidden(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return ForbiddenExact.Contains(key) || key.Contains("cvv", StringComparison.Ordinal) || key.Contains("cvc", StringComparison.Ordinal)
               || key.Contains("csc", StringComparison.Ordinal);
    }

    public static bool IsKnown(string collection, string field) => Fields.TryGetValue(collection, out var fields) && fields.ContainsKey(field);

    // Recusa (ArgumentException, com o nome do campo e nunca o valor) um campo proibido em qualquer nível.
    public static void RejectForbidden(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    if (IsForbidden(name)) throw new ArgumentException($"O campo «{name}» (código de segurança do cartão) nunca se sincroniza.");
                    RejectForbidden(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array) RejectForbidden(item);
                break;
        }
    }

    // Os campos conhecidos têm o tipo certo (texto, inteiro, verdadeiro/falso) ou são null.
    public static bool IsValid(string collection, JsonObject data)
    {
        if (!Fields.TryGetValue(collection, out var fields)) return false;
        foreach (var (name, type) in fields)
        {
            if (data[name] is not { } value) continue;
            if (value is not JsonValue v) return false;
            var ok = type switch
            {
                SecretFieldType.Text => v.TryGetValue<string>(out _),
                SecretFieldType.Integer => v.TryGetValue<int>(out _),
                _ => v.TryGetValue<bool>(out _),
            };
            if (!ok) return false;
        }
        return true;
    }

    // Só os campos conhecidos, por ordem de nome, sem nulos: dois conteúdos iguais têm a mesma forma (a impressão que o
    // perfil local compara com a base, ProfileSync).
    public static string Canonical(string collection, JsonObject data)
    {
        var known = new JsonObject();
        if (Fields.TryGetValue(collection, out var fields))
            foreach (var name in fields.Keys)
                if (name != LastUsedField && data[name] is { } value) known[name] = value.DeepClone();
        return CanonicalJson.Of(known);
    }
}
