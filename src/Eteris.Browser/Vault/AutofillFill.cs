using System.Globalization;

namespace Eteris.Browser.Vault;

// A lista que aparece num campo que não é de palavra-passe (C6).
public enum AutofillMode { None, Contacts, Cards, Ibans, TaxIds }

// O que se escolhe preencher (lista ou menu de contexto «Preencher com…»).
public enum FillCategory { Login, Contact, Card, Iban, Document, Travel }

// Os interruptores «Preencher automaticamente…» de cada secção de eteris://autofill (só controlam as sugestões).
public sealed record AutofillSwitches(bool Contacts, bool Payments, bool Documents);

// Preencher contactos, pagamentos, documentos e viagens (docs/palavras-passe.md, C6; desenho, secção 3.6). Decisões puras.
public static class AutofillPolicy
{
    public static readonly IReadOnlySet<string> ContactKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "given-name", "family-name", "organization", "street-address", "address-line2", "address-line3", "address-number",
        "address-complement", "neighborhood", "city", "region", "postal-code", "country", "tel", "email",
    };

    // O CVC (cc-csc) não está: nunca se preenche nem se oferece nada nele.
    public static readonly IReadOnlySet<string> CardKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "cc-number", "cc-name", "cc-exp", "cc-exp-month", "cc-exp-year",
    };

    // Contactos: num formulário de morada (ou de pagamento com morada); cartões: num formulário de pagamento; IBAN e o
    // número fiscal (NIF/CPF, D-2): em qualquer formulário. Os outros documentos e as viagens não se detetam sozinhos (só
    // pelo menu de contexto). Cada secção desligada não mostra a lista dela.
    public static AutofillMode ModeFor(string? kind, string? formKind, AutofillSwitches on)
    {
        if (kind is null) return AutofillMode.None;
        if (CardKinds.Contains(kind)) return formKind == "payment" && on.Payments ? AutofillMode.Cards : AutofillMode.None;
        if (kind == "iban") return on.Payments ? AutofillMode.Ibans : AutofillMode.None;
        if (kind == "tax-id") return on.Documents ? AutofillMode.TaxIds : AutofillMode.None;
        if (ContactKinds.Contains(kind) && formKind is "address" or "payment") return on.Contacts ? AutofillMode.Contacts : AutofillMode.None;
        return AutofillMode.None;
    }

    public static FillCategory CategoryOf(AutofillMode mode) => mode switch
    {
        AutofillMode.Cards => FillCategory.Card,
        AutofillMode.Ibans => FillCategory.Iban,
        AutofillMode.TaxIds => FillCategory.Document,
        _ => FillCategory.Contact,
    };

    // Cartões, IBAN e documentos nunca em http (o próprio computador conta como seguro).
    public static bool AllowedOn(FillCategory category, string pageOrigin)
        => category is not (FillCategory.Card or FillCategory.Iban or FillCategory.Document) || Origins.IsSecure(pageOrigin);

    // A reautenticação de cada categoria (pede-se se o interruptor de pagamentos estiver ligado, D-5).
    public static ReauthPurpose? Reauth(FillCategory category) => category switch
    {
        FillCategory.Card or FillCategory.Iban => ReauthPurpose.FillPayment,
        FillCategory.Document => ReauthPurpose.FillDocument,
        _ => null,
    };

    // Os tipos de campo de cada categoria: no menu de contexto, um campo destes preenche o formulário inteiro; outro campo
    // recebe só o valor principal do item.
    public static bool FillsForm(FillCategory category, string? kind) => kind is not null && category switch
    {
        FillCategory.Login => kind is "username" or "email" or "password",
        FillCategory.Contact => ContactKinds.Contains(kind),
        FillCategory.Card => CardKinds.Contains(kind),
        FillCategory.Iban => kind == "iban",
        FillCategory.Document => kind == "tax-id",
        _ => false,
    };

    public static string ManageUrl(FillCategory category) => category switch
    {
        FillCategory.Login => "eteris://passwords/",
        FillCategory.Card or FillCategory.Iban => "eteris://autofill/#pagamentos",
        FillCategory.Document => "eteris://autofill/#documentos",
        FillCategory.Travel => "eteris://autofill/#viagens",
        _ => "eteris://autofill/#contacto",
    };
}

// Os valores por tipo de campo do script (cofre.js, Classifier.KINDS). Nunca o código de segurança do cartão.
public static class AutofillValues
{
    private static readonly Dictionary<string, string> s_countries = new(StringComparer.OrdinalIgnoreCase) { ["PT"] = "Portugal", ["BR"] = "Brasil" };

    public static Dictionary<string, string> Contact(ContactItem c)
    {
        var v = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string kind, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) v[kind] = value.Trim();
        }
        Put("given-name", c.GivenName);
        Put("family-name", c.FamilyName);
        Put("name", string.Join(' ', new[] { c.GivenName, c.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())));
        Put("organization", c.Organization);
        Put("street-address", c.Street);
        Put("address-number", c.Number);
        Put("address-complement", c.Complement);
        Put("address-line2", c.Complement);
        Put("neighborhood", c.Neighborhood);
        Put("city", c.City);
        Put("region", c.Region);
        Put("postal-code", c.PostalCode);
        if (!string.IsNullOrWhiteSpace(c.Country))
        {
            Put("country", s_countries.GetValueOrDefault(c.Country.Trim(), c.Country));
            Put("country:alt", c.Country.Trim().ToUpperInvariant());
        }
        Put("tel", c.Phone);
        Put("email", c.Email);
        return v;
    }

    public static Dictionary<string, string> Card(CardItem card)
    {
        var month = card.ExpMonth.ToString("00", CultureInfo.InvariantCulture);
        var year = card.ExpYear.ToString("0000", CultureInfo.InvariantCulture);
        var v = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cc-number"] = VaultFields.Digits(card.Number),
            ["cc-exp-month"] = month,
            ["cc-exp-year"] = year,
            ["cc-exp"] = month + "/" + year[^2..],
        };
        if (!string.IsNullOrWhiteSpace(card.Name)) v["cc-name"] = card.Name.Trim();
        return v;
    }

    public static Dictionary<string, string> Iban(IbanItem iban) => new(StringComparer.Ordinal) { ["iban"] = IbanRules.Format(iban.Iban) };

    public static Dictionary<string, string> TaxId(IdDocItem doc) => new(StringComparer.Ordinal) { ["tax-id"] = TaxIdRules.Format(doc.Number, doc.Country) };

    // O valor principal de um item, para um só campo (menu de contexto num campo de outro tipo).
    public static string Single(object item) => item switch
    {
        CardItem card => VaultFields.Digits(card.Number),
        IbanItem iban => IbanRules.Format(iban.Iban),
        IdDocItem { Type: IdDocType.TaxId } doc => TaxIdRules.Format(doc.Number, doc.Country),
        IdDocItem doc => doc.Number,
        TravelItem { Type: TravelType.Vehicle } t => t.Plate ?? t.Vin ?? string.Empty,
        TravelItem t => t.Number ?? string.Empty,
        ContactItem c => Contact(c).GetValueOrDefault("name") ?? c.Organization ?? string.Empty,
        _ => string.Empty,
    };
}

// As linhas da lista (e do menu de contexto): mascaradas, nunca o número inteiro.
public static class AutofillRows
{
    public static SuggestionRow Contact(ContactItem c)
    {
        var name = string.Join(' ', new[] { c.GivenName, c.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        var street = string.Join(' ', new[] { c.Street, c.Number }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        var place = string.Join(", ", new[] { street, c.City }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        var title = name.Length > 0 ? name : c.Organization ?? c.Label ?? "Contacto";
        return new SuggestionRow(c.Id, title, place.Length > 0 ? place : c.Email ?? c.Label ?? string.Empty, false, OriginMatch.Exact) { Glyph = "" };
    }

    public static SuggestionRow Card(CardItem card)
        => new(card.Id, $"{card.Brand} {CardRules.Mask(card.Number)}",
            $"{card.ExpMonth:00}/{card.ExpYear % 100:00}" + (string.IsNullOrWhiteSpace(card.Nickname) ? string.Empty : " · " + card.Nickname),
            false, OriginMatch.Exact) { Glyph = "" };

    public static SuggestionRow Iban(IbanItem iban)
        => new(iban.Id, IbanRules.Mask(iban.Iban), string.IsNullOrWhiteSpace(iban.Nickname) ? "IBAN" : iban.Nickname!, false, OriginMatch.Exact) { Glyph = "" };

    public static SuggestionRow Document(IdDocItem doc)
        => new(doc.Id, doc.Summary, doc.Name ?? string.Empty, false, OriginMatch.Exact) { Glyph = "" };

    // A partir dos metadados do cofre (sem decifrar números): para a lista e o menu de contexto.
    public static SuggestionRow? FromEntry(VaultEntry e)
    {
        string? T(string name) => e.Meta[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int? N(string name) => e.Meta[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        switch (e.Collection)
        {
            case VaultCollection.Contacts:
                return Contact(new ContactItem(e.Id, T("label"), T("givenName"), T("familyName"), T("organization"), T("street"), T("number"),
                    T("complement"), T("neighborhood"), T("city"), T("region"), T("postalCode"), T("country"), T("email"), T("phone"), e.Created, null));
            case VaultCollection.Cards:
                var nickname = T("nickname");
                return new SuggestionRow(e.Id, $"{T("brand") ?? "Cartão"} •••• {T("last4")}",
                    $"{N("expMonth") ?? 0:00}/{(N("expYear") ?? 0) % 100:00}" + (string.IsNullOrWhiteSpace(nickname) ? string.Empty : " · " + nickname),
                    false, OriginMatch.Exact) { Glyph = "\uE8C7" };
            case VaultCollection.Ibans:
                return new SuggestionRow(e.Id, T("masked") ?? "IBAN", T("nickname") ?? "IBAN", false, OriginMatch.Exact) { Glyph = "\uE825" };
            case VaultCollection.IdDocs:
                var type = IdDocItem.ParseType(T("type")) ?? IdDocType.NationalId;
                return new SuggestionRow(e.Id, $"{IdDocItem.TypeName(type, T("country"))} {T("masked")}", T("name") ?? string.Empty, false, OriginMatch.Exact) { Glyph = "\uE779" };
            case VaultCollection.Travel:
                var vehicle = T("type") == "vehicle";
                var title = vehicle
                    ? string.Join(" · ", new[] { string.Join(' ', new[] { T("make"), T("model") }.Where(s => !string.IsNullOrWhiteSpace(s))), T("plate") ?? string.Empty }.Where(s => s.Length > 0))
                    : string.Join(" · ", new[] { T("company"), T("program"), T("masked") }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return new SuggestionRow(e.Id, title.Length > 0 ? title : "Viagem", vehicle ? "Veículo" : "Programa de fidelidade", false, OriginMatch.Exact) { Glyph = "\uE804" };
            default:
                return null;
        }
    }

    public static SuggestionRow Travel(TravelItem t)
        => new(t.Id, t.Summary, t.Type == TravelType.Vehicle ? "Veículo" : "Programa de fidelidade", false, OriginMatch.Exact) { Glyph = "" };
}
