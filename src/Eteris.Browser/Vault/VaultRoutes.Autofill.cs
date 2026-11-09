using System.Globalization;
using System.Text.Json.Nodes;

namespace Eteris.Browser.Vault;

// eteris://autofill/ (docs/palavras-passe.md, B3): Pagamentos (cartões sem código de segurança, IBAN), Informações de
// contacto, Documentos de identificação (passaporte, carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF) e Viagens
// (veículos, programas de fidelidade). Mesmas regras que as palavras-passe: números completos só depois da
// reautenticação, editar um item com segredo também, apagar com «Anular», token, nunca na janela privada.
public sealed partial class VaultRoutes
{
    private static readonly string[] AutofillSwitches = ["afPayments", "afContacts", "afDocuments", "afTravel", "payAskHello"];

    private async Task<VaultReply> AutofillAsync(VaultRequest r)
    {
        if (r.IsPrivate) return r.Path == "/" ? VaultReply.Html(PrivatePage(PrivateMessage)) : VaultReply.Refused;
        if (r.Path == "/") return VaultReply.Html(Browser.AutofillPage.Render(AutofillData()));
        if (!r.Authorized) return VaultReply.Refused;
        var collection = r.Q("c") switch
        {
            "cards" => VaultCollection.Cards,
            "ibans" => VaultCollection.Ibans,
            "contacts" => VaultCollection.Contacts,
            "idDocs" => VaultCollection.IdDocs,
            "travel" => VaultCollection.Travel,
            _ => (VaultCollection?)null,
        };
        switch (r.Path)
        {
            case "/list":
                return VaultReply.Json(AutofillData());
            case "/reveal" when collection is { } c && r.Q("id") is { } id:
            {
                if (c != VaultCollection.Contacts && !await AskAsync(r, ReauthPurpose.Reveal).ConfigureAwait(false)) return NotConfirmed();
                if (FullItem(c, id) is not { } item) return Gone();
                var hold = c != VaultCollection.Contacts ? HoldSecretOnScreen() : null;
                return VaultReply.Json(new { ok = true, item, hold });
            }
            case "/hide":
                ReleaseSecretOnScreen(r.Q("h"));
                return VaultReply.Json(new { ok = true });
            case "/keep" when r.Q("h") is { } keep:
                return VaultReply.Json(new { ok = KeepSecretOnScreen(keep) });
            case "/save" when collection is { } c:
                return await SaveItemAsync(r, c).ConfigureAwait(false);
            case "/delete" when collection is { } c && r.Q("id") is { } id:
                return Delete(c, id);
            case "/undo":
                return VaultReply.Json(new { ok = Undo(r.Q("u")) });
            case "/set" when r.Q("k") is { } key && r.Q("v") is { } value && AutofillSwitches.Contains(key):
            {
                // Desligar «Pedir o Windows Hello ao preencher pagamentos» também pede o Windows Hello.
                if (Data.UserSettings.NeedsReauth(key, value) && !await AskAsync(r, ReauthPurpose.ChangePaymentSetting).ConfigureAwait(false))
                    return VaultReply.Refused;
                return VaultReply.Text(host.SetSetting(key, value) ? "ok" : "recusado");
            }
            default:
                return VaultReply.Refused;
        }
    }

    // Os dados da página: só metadados e números mascarados.
    private object AutofillData()
    {
        var settings = host.Settings;
        var now = DateTimeOffset.UtcNow;
        var cards = Visible(VaultCollection.Cards).Select(e =>
        {
            int month = Int(e.Meta, "expMonth") ?? 0, year = Int(e.Meta, "expYear") ?? 0;
            return new
            {
                id = e.Id,
                brand = Text(e.Meta, "brand") ?? "Cartão",
                masked = "•••• " + (Text(e.Meta, "last4") ?? string.Empty),
                exp = month is >= 1 and <= 12 ? $"{month:00}/{year % 100:00}" : string.Empty,
                expired = month is >= 1 and <= 12 && year > 0 && CardRules.IsExpired(month, year, now),
                name = Text(e.Meta, "name"),
                nickname = Text(e.Meta, "nickname"),
            };
        }).ToList();
        var ibans = Visible(VaultCollection.Ibans).Select(e => new { id = e.Id, masked = Text(e.Meta, "masked"), nickname = Text(e.Meta, "nickname") }).ToList();
        var contacts = Visible(VaultCollection.Contacts).Select(e => vault.GetContact(e.Id)).OfType<ContactItem>().Select(c => new
        {
            id = c.Id, label = c.Label, summary = c.Summary, givenName = c.GivenName, familyName = c.FamilyName, organization = c.Organization,
            street = c.Street, number = c.Number, complement = c.Complement, neighborhood = c.Neighborhood, city = c.City, region = c.Region,
            postalCode = c.PostalCode, country = c.Country, email = c.Email, phone = c.Phone,
        }).ToList();
        var docs = Visible(VaultCollection.IdDocs).Select(e =>
        {
            var type = IdDocItem.ParseType(Text(e.Meta, "type")) ?? IdDocType.NationalId;
            var expires = Text(e.Meta, "expires");
            return new
            {
                id = e.Id,
                type = IdDocItem.TypeCode(type),
                typeName = IdDocItem.TypeName(type, Text(e.Meta, "country")),
                masked = Text(e.Meta, "masked"),
                name = Text(e.Meta, "name"),
                country = Text(e.Meta, "country"),
                expires = expires is null ? null : DisplayDay(expires),
                expired = expires is not null && DateOnly.TryParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                          && d < DateOnly.FromDateTime(now.UtcDateTime),
            };
        }).ToList();
        var travel = Visible(VaultCollection.Travel).Select(e =>
        {
            var vehicle = Text(e.Meta, "type") == "vehicle";
            return new
            {
                id = e.Id,
                type = vehicle ? "vehicle" : "loyalty",
                title = vehicle
                    ? string.Join(' ', new[] { Text(e.Meta, "make"), Text(e.Meta, "model"), Int(e.Meta, "year")?.ToString(CultureInfo.InvariantCulture) }.Where(s => !string.IsNullOrWhiteSpace(s)))
                    : string.Join(" · ", new[] { Text(e.Meta, "company"), Text(e.Meta, "program") }.Where(s => !string.IsNullOrWhiteSpace(s))),
                plate = Text(e.Meta, "plate"),
                masked = Text(e.Meta, "masked"),
                owner = Text(e.Meta, vehicle ? "owner" : "name"),
            };
        }).ToList();
        return new
        {
            token = Browser.InternalPages.Token,
            cards, ibans, contacts, docs, travel,
            docTypes = new[] { IdDocType.Passport, IdDocType.DriverLicense, IdDocType.NationalId, IdDocType.TaxId }
                .Select(t => new { code = IdDocItem.TypeCode(t), name = IdDocItem.TypeName(t, null), pt = IdDocItem.TypeName(t, "PT"), br = IdDocItem.TypeName(t, "BR") }),
            addressForms = new[] { "PT", "BR", "" }.ToDictionary(c => c, c => Browser.AutofillPage.AddressForm(c.Length == 0 ? null : c)
                .Select(f => new { key = f.Key, label = f.Label, placeholder = f.Placeholder, wide = f.Wide })),
            afPayments = settings.AutofillPayments,
            afContacts = settings.AutofillContacts,
            afDocuments = settings.AutofillDocuments,
            afTravel = settings.AutofillTravel,
            payAskHello = settings.PaymentsAskHello,
            advice = gate.Advice,
        };
    }

    private IEnumerable<VaultEntry> Visible(VaultCollection collection)
    {
        var hidden = Hidden(collection);
        return vault.List(collection).Where(e => !hidden.Contains(e.Id)).OrderBy(e => e.Created);
    }

    // O item inteiro para ver ou editar (com o número completo, já formatado).
    private object? FullItem(VaultCollection collection, string id) => collection switch
    {
        VaultCollection.Cards => vault.GetCard(id) is { } c
            ? new { name = c.Name, number = FormatCard(c.Number), exp = $"{c.ExpMonth:00}/{c.ExpYear % 100:00}", nickname = c.Nickname, brand = c.Brand } : null,
        VaultCollection.Ibans => vault.GetIban(id) is { } i ? new { iban = IbanRules.Format(i.Iban), nickname = i.Nickname } : null,
        VaultCollection.Contacts => vault.GetContact(id) is { } c
            ? new
            {
                label = c.Label, givenName = c.GivenName, familyName = c.FamilyName, organization = c.Organization, street = c.Street, number = c.Number,
                complement = c.Complement, neighborhood = c.Neighborhood, city = c.City, region = c.Region, postalCode = c.PostalCode, country = c.Country,
                email = c.Email, phone = c.Phone,
            } : null,
        VaultCollection.IdDocs => vault.GetIdDoc(id) is { } d
            ? new
            {
                type = IdDocItem.TypeCode(d.Type), number = d.Type == IdDocType.TaxId ? TaxIdRules.Format(d.Number, d.Country) : d.Number, name = d.Name,
                country = d.Country, issuer = d.Issuer, issued = d.Issued?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                expires = d.Expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), note = d.Note,
            } : null,
        VaultCollection.Travel => vault.GetTravel(id) is { } t
            ? new
            {
                type = t.Type == TravelType.Vehicle ? "vehicle" : "loyalty", make = t.Make, model = t.Model, year = t.Year, owner = t.Owner, plate = t.Plate,
                plateRegion = t.PlateRegion, vin = t.Vin, company = t.Company, program = t.Program, number = t.Number, name = t.Name,
            } : null,
        _ => null,
    };

    private static string FormatCard(string digits)
        => digits.Length == 15 && digits[0] == '3'
            ? $"{digits[..4]} {digits[4..10]} {digits[10..]}"
            : string.Join(' ', Enumerable.Range(0, (digits.Length + 3) / 4).Select(i => digits.Substring(i * 4, Math.Min(4, digits.Length - i * 4))));

    private async Task<VaultReply> SaveItemAsync(VaultRequest r, VaultCollection collection)
    {
        // Nunca o código de segurança do cartão: um campo com esse nome recusa o pedido inteiro.
        if (r.Form.Keys.FirstOrDefault(VaultFields.IsForbidden) is { } forbidden)
            return Invalid($"O campo «{forbidden}» é o código de segurança do cartão, que nunca se guarda.");
        var id = r.F("id");
        var editing = !string.IsNullOrEmpty(id);
        if (editing)
        {
            if (vault.List(collection).All(e => e.Id != id)) return Gone();
            if (collection != VaultCollection.Contacts && !await AskAsync(r, ReauthPurpose.Reveal).ConfigureAwait(false)) return NotConfirmed();
        }
        string? F(string name) => r.F(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        try
        {
            switch (collection)
            {
                case VaultCollection.Cards:
                {
                    if (!CardRules.IsValidNumber(F("number"))) return Invalid("Número de cartão inválido: confira os algarismos.");
                    if (CardRules.ParseExpiry(F("exp")) is not { } exp || exp.Year > 2100) return Invalid("Validade inválida: escreva o mês e o ano (MM/AA).");
                    var old = editing ? vault.GetCard(id!) : null;
                    var saved = vault.Save(new CardItem(id ?? string.Empty, F("name"), F("number")!, exp.Month, exp.Year, F("nickname"), old?.Created ?? default, old?.Used));
                    return Saved(saved, editing);
                }
                case VaultCollection.Ibans:
                    if (!IbanRules.IsValid(F("iban"))) return Invalid("IBAN inválido: confira o país e os algarismos.");
                    return Saved(vault.Save(new IbanItem(id ?? string.Empty, F("iban")!, F("nickname"), editing ? vault.GetIban(id!)?.Created ?? default : default)), editing);
                case VaultCollection.Contacts:
                {
                    var country = F("country")?.ToUpperInvariant();
                    string[] keys = ["givenName", "familyName", "organization", "street", "number", "complement", "neighborhood", "city", "region", "postalCode", "email", "phone"];
                    if (keys.All(k => F(k) is null)) return Invalid("Preencha pelo menos um campo.");
                    if (F("postalCode") is { } postal && !ContactRules.IsValidPostalCode(postal, country))
                        return Invalid(country == "BR" ? "CEP inválido: 00000-000." : country == "PT" ? "Código postal inválido: 0000-000." : "Código postal inválido.");
                    if (F("email") is { } email && !ContactRules.IsValidEmail(email)) return Invalid("E-mail inválido.");
                    var old = editing ? vault.GetContact(id!) : null;
                    return Saved(vault.Save(new ContactItem(id ?? string.Empty, F("label"), F("givenName"), F("familyName"), F("organization"), F("street"),
                        F("number"), F("complement"), country == "PT" ? null : F("neighborhood"), F("city"), F("region"), F("postalCode"), country, F("email"),
                        F("phone"), old?.Created ?? default, old?.Used)), editing);
                }
                case VaultCollection.IdDocs:
                {
                    if (IdDocItem.ParseType(F("type")) is not { } type) return Invalid("Escolha o tipo de documento.");
                    var country = F("country")?.ToUpperInvariant();
                    if (F("number") is not { } number) return Invalid("Escreva o número do documento.");
                    if (type == IdDocType.TaxId && !TaxIdRules.IsValid(number, country))
                        return Invalid(country == "BR" ? "CPF inválido: confira os dígitos de controlo." : country == "PT" ? "NIF inválido: confira o dígito de controlo." : "Número fiscal inválido.");
                    if (!TryDay(F("issued"), out var issued) || !TryDay(F("expires"), out var expires)) return Invalid("Data inválida: use o calendário (dia, mês e ano).");
                    return Saved(vault.Save(new IdDocItem(id ?? string.Empty, type, number, F("name"), country, F("issuer"), issued, expires, F("note"))), editing);
                }
                case VaultCollection.Travel:
                {
                    if (F("type") == "vehicle")
                    {
                        int? year = null;
                        if (F("year") is { } y)
                        {
                            if (!int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1900 or > 2100) return Invalid("Ano inválido: quatro algarismos.");
                            year = parsed;
                        }
                        var region = F("plateRegion")?.ToUpperInvariant();
                        var plate = F("plate")?.ToUpperInvariant();
                        if (plate is not null && !TravelRules.IsValidPlate(plate, region))
                            return Invalid(region == "BR" ? "Placa inválida: ABC-1234 ou ABC1D23." : region == "PT" ? "Matrícula inválida: AA-00-AA, 00-AA-00, 00-00-AA ou AA-00-00." : "Matrícula inválida.");
                        if (new[] { F("make"), F("model"), plate, F("vin") }.All(s => s is null)) return Invalid("Preencha a marca, o modelo, a matrícula/placa ou o VIN.");
                        return Saved(vault.Save(TravelItem.Vehicle(id ?? string.Empty, F("make"), F("model"), year, F("owner"), plate, region, F("vin")?.ToUpperInvariant())), editing);
                    }
                    if (new[] { F("company"), F("program"), F("number") }.All(s => s is null)) return Invalid("Preencha a companhia, o programa ou o número.");
                    return Saved(vault.Save(TravelItem.Loyalty(id ?? string.Empty, F("company"), F("program"), F("number"), F("name"))), editing);
                }
                default:
                    return VaultReply.Refused;
            }
        }
        catch (ArgumentException e)
        {
            return Invalid(e.Message.Split(" (Parameter", 2)[0]);
        }
    }

    private static VaultReply Saved(string id, bool editing) => VaultReply.Json(new { ok = true, id, message = editing ? "Alterações guardadas." : "Guardado." });

    private static bool TryDay(string? text, out DateOnly? day)
    {
        day = null;
        if (text is null) return true;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        day = d;
        return true;
    }

    private static string DisplayDay(string isoDay)
        => DateOnly.TryParseExact(isoDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : isoDay;

    private static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
