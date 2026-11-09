using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Vault;

// Modelos do cofre (docs/palavras-passe.md). Os nomes das coleções são os da sincronização (desenho, secção 4.1).
// Os ToString() dos itens só dizem o tipo e o ID: um item nunca leva um segredo para um registo ou um diagnóstico.
public enum VaultCollection { Logins, LoginsNever, Cards, Ibans, Contacts, IdDocs, Travel }

public static class VaultCollections
{
    private static readonly (VaultCollection Collection, string Name)[] Names =
    [
        (VaultCollection.Logins, "logins"), (VaultCollection.LoginsNever, "loginsNever"), (VaultCollection.Cards, "cards"),
        (VaultCollection.Ibans, "ibans"), (VaultCollection.Contacts, "contacts"), (VaultCollection.IdDocs, "idDocs"),
        (VaultCollection.Travel, "travel"),
    ];

    public static IReadOnlyList<VaultCollection> All { get; } = Names.Select(n => n.Collection).ToArray();

    public static string Name(VaultCollection collection) => Names.First(n => n.Collection == collection).Name;

    public static VaultCollection? Parse(string? name)
    {
        foreach (var (collection, known) in Names)
            if (string.Equals(known, name, StringComparison.Ordinal)) return collection;
        return null;
    }
}

public static class VaultIds
{
    // 128 bits aleatórios em hexadecimal: nada do site nem do utilizador.
    public static string New() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

// Campos que nunca se guardam nem sincronizam: o código de segurança do cartão (CVV/CVC/CSC/CID).
public static class VaultFields
{
    private static readonly HashSet<string> Exact = new(StringComparer.Ordinal) { "cid", "cvn", "securitycode", "cardsecuritycode", "cardverificationcode", "cardverificationvalue" };

    public static bool IsForbidden(string name)
    {
        var key = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Exact.Contains(key) || key.Contains("cvv", StringComparison.Ordinal) || key.Contains("cvc", StringComparison.Ordinal)
               || key.Contains("csc", StringComparison.Ordinal);
    }

    // A mensagem diz o nome do campo, nunca o valor.
    public static void RejectForbidden(IEnumerable<KeyValuePair<string, string?>> fields)
    {
        foreach (var (name, _) in fields)
            if (IsForbidden(name)) throw new ArgumentException($"O campo «{name}» (código de segurança do cartão) nunca se guarda.", nameof(fields));
    }

    internal static string Digits(string? text) => new((text ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    internal static string Alnum(string? text) => new((text ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    // «•••• 4242»: os últimos quatro carateres (letras e algarismos) de um número.
    internal static string MaskTail(string? number, int keep = 4)
    {
        var plain = Alnum(number);
        return "•••• " + (plain.Length <= keep ? plain : plain[^keep..]);
    }
}

public static class CardRules
{
    public static bool IsValidNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number) || number.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return false;
        var digits = VaultFields.Digits(number);
        return digits.Length is >= 12 and <= 19 && Luhn(digits);
    }

    public static bool Luhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return digits.Length > 0 && sum % 10 == 0;
    }

    // Elo e Hipercard antes das gerais (os prefixos deles caem dentro de outras gamas).
    private static readonly (string Brand, Func<string, bool> Test)[] Brands =
    [
        ("Elo", d => StartsWithAny(d, "401178", "401179", "431274", "438935", "451416", "457393", "457631", "457632", "504175", "627780", "636297", "636368")
                     || InRange(d, 6, 506699, 506778) || InRange(d, 6, 509000, 509999) || InRange(d, 6, 650031, 650033) || InRange(d, 6, 650035, 650051)
                     || InRange(d, 6, 650405, 650439) || InRange(d, 6, 650485, 650538) || InRange(d, 6, 650541, 650598) || InRange(d, 6, 650700, 650718)
                     || InRange(d, 6, 650720, 650727) || InRange(d, 6, 650901, 650978) || InRange(d, 6, 651652, 651679) || InRange(d, 6, 655000, 655019)
                     || InRange(d, 6, 655021, 655058)),
        ("Hipercard", d => StartsWithAny(d, "606282", "3841")),
        ("Visa", d => d.StartsWith('4')),
        ("Mastercard", d => InRange(d, 2, 51, 55) || InRange(d, 4, 2221, 2720)),
        ("American Express", d => StartsWithAny(d, "34", "37")),
        ("Diners Club", d => StartsWithAny(d, "36", "38", "39") || InRange(d, 3, 300, 305)),
        ("JCB", d => InRange(d, 4, 3528, 3589)),
        ("Discover", d => StartsWithAny(d, "6011", "65") || InRange(d, 3, 644, 649)),
        ("Maestro", d => StartsWithAny(d, "50", "56", "57", "58", "6")),
    ];

    public static string Brand(string? number)
    {
        var digits = VaultFields.Digits(number);
        foreach (var (brand, test) in Brands)
            if (test(digits)) return brand;
        return "Cartão";
    }

    public static string Mask(string? number) => VaultFields.MaskTail(VaultFields.Digits(number));

    // «08/29», «8/2029», «08-29», «0829» → (8, 2029).
    public static (int Month, int Year)? ParseExpiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        var m = Regex.Match(compact, @"^(\d{1,2})[/\-.](\d{2}|\d{4})$");
        if (!m.Success) m = Regex.Match(compact, @"^(\d{2})(\d{2})$");
        if (!m.Success) return null;
        var month = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (year < 100) year += 2000;
        return month is >= 1 and <= 12 ? (month, year) : null;
    }

    // Vale até ao fim do mês da validade.
    public static bool IsExpired(int month, int year, DateTimeOffset now)
        => now.UtcDateTime >= new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

    private static bool StartsWithAny(string digits, params string[] prefixes) => prefixes.Any(p => digits.StartsWith(p, StringComparison.Ordinal));

    private static bool InRange(string digits, int length, int low, int high)
        => digits.Length >= length && int.TryParse(digits.AsSpan(0, length), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= low && v <= high;
}

public static class IbanRules
{
    // Comprimento do IBAN por país (registo SWIFT): os da Europa e o do Brasil.
    private static readonly Dictionary<string, int> Lengths = new(StringComparer.Ordinal)
    {
        ["AD"] = 24, ["AT"] = 20, ["BE"] = 16, ["BG"] = 22, ["BR"] = 29, ["CH"] = 21, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22,
        ["DK"] = 18, ["EE"] = 20, ["ES"] = 24, ["FI"] = 18, ["FR"] = 27, ["GB"] = 22, ["GI"] = 23, ["GR"] = 27, ["HR"] = 21,
        ["HU"] = 28, ["IE"] = 22, ["IS"] = 26, ["IT"] = 27, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21, ["MC"] = 27,
        ["MT"] = 31, ["NL"] = 18, ["NO"] = 15, ["PL"] = 28, ["PT"] = 25, ["RO"] = 24, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24,
        ["SM"] = 27, ["VA"] = 22, ["AO"] = 25, ["CV"] = 25, ["MZ"] = 25, ["ST"] = 25, ["TL"] = 23,
    };

    public static string Normalize(string? iban) => VaultFields.Alnum(iban);

    public static bool IsValid(string? iban)
    {
        var s = Normalize(iban);
        if (s.Length < 15 || !char.IsAsciiLetter(s[0]) || !char.IsAsciiLetter(s[1]) || !char.IsAsciiDigit(s[2]) || !char.IsAsciiDigit(s[3])) return false;
        if (Lengths.TryGetValue(s[..2], out var length) ? s.Length != length : s.Length > 34) return false;
        var remainder = 0;
        foreach (var c in s[4..] + s[..4])
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value >= 10 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }

    // «PT50 0002 0123 …» em grupos de quatro.
    public static string Format(string? iban)
    {
        var s = Normalize(iban);
        return string.Join(' ', Enumerable.Range(0, (s.Length + 3) / 4).Select(i => s.Substring(i * 4, Math.Min(4, s.Length - i * 4))));
    }

    // «PT50 •••• 0154»: o país e os dígitos de controlo, e os últimos quatro.
    public static string Mask(string? iban)
    {
        var s = Normalize(iban);
        return s.Length < 8 ? VaultFields.MaskTail(s) : s[..4] + " " + VaultFields.MaskTail(s);
    }
}

// D-2: o número fiscal — NIF (Portugal) e CPF (Brasil), com dígito de controlo; de outros países aceita-se como vem.
public static class TaxIdRules
{
    private static readonly string[] NifStarts = ["1", "2", "3", "5", "6", "8", "9", "45", "70", "71", "72", "74", "75", "77", "79", "90", "91", "98", "99"];

    public static bool IsValidNif(string? nif)
    {
        if (nif is null || nif.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '.' or '-'))) return false;
        var d = VaultFields.Digits(nif);
        if (d.Length != 9 || !NifStarts.Any(p => d.StartsWith(p, StringComparison.Ordinal))) return false;
        var sum = 0;
        for (var i = 0; i < 8; i++) sum += (d[i] - '0') * (9 - i);
        var check = 11 - sum % 11;
        if (check >= 10) check = 0;
        return d[8] - '0' == check;
    }

    public static bool IsValidCpf(string? cpf)
    {
        if (cpf is null || cpf.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '.' or '-'))) return false;
        var d = VaultFields.Digits(cpf);
        if (d.Length != 11 || d.All(c => c == d[0])) return false;
        for (var n = 9; n <= 10; n++)
        {
            var sum = 0;
            for (var i = 0; i < n; i++) sum += (d[i] - '0') * (n + 1 - i);
            var check = sum * 10 % 11 % 10;
            if (d[n] - '0' != check) return false;
        }
        return true;
    }

    public static bool IsValid(string? number, string? country) => country?.ToUpperInvariant() switch
    {
        "PT" => IsValidNif(number),
        "BR" => IsValidCpf(number),
        _ => !string.IsNullOrWhiteSpace(number),
    };

    public static string Format(string? number, string? country)
    {
        var d = VaultFields.Digits(number);
        return country?.ToUpperInvariant() switch
        {
            "BR" when d.Length == 11 => $"{d[..3]}.{d[3..6]}.{d[6..9]}-{d[9..]}",
            "PT" when d.Length == 9 => $"{d[..3]} {d[3..6]} {d[6..]}",
            _ => (number ?? string.Empty).Trim(),
        };
    }
}

public static class ContactRules
{
    public static bool IsValidPostalCode(string? code, string? country)
    {
        var c = (code ?? string.Empty).Trim();
        return country?.ToUpperInvariant() switch
        {
            "PT" => Regex.IsMatch(c, @"^\d{4}-\d{3}$"),
            "BR" => Regex.IsMatch(c, @"^\d{5}-?\d{3}$"),
            _ => Regex.IsMatch(c, @"^[A-Za-z0-9][A-Za-z0-9 \-]{1,9}$"),
        };
    }

    public static string NormalizePostalCode(string? code, string? country)
    {
        var c = (code ?? string.Empty).Trim();
        var d = VaultFields.Digits(c);
        return country?.ToUpperInvariant() switch
        {
            "BR" when d.Length == 8 => d[..5] + "-" + d[5..],
            "PT" when d.Length == 7 => d[..4] + "-" + d[4..],
            _ => c,
        };
    }

    public static bool IsValidEmail(string? email) => email is not null && Regex.IsMatch(email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}

public static class TravelRules
{
    // Matrículas de Portugal (AA-00-AA, 00-AA-00, 00-00-AA, AA-00-00) e placas do Brasil (ABC-1234 e Mercosul ABC1D23).
    public static bool IsValidPlate(string? plate, string? country)
    {
        var p = (plate ?? string.Empty).Trim().ToUpperInvariant();
        return country?.ToUpperInvariant() switch
        {
            "PT" => Regex.IsMatch(p, @"^([A-Z]{2}-\d{2}-[A-Z]{2}|\d{2}-[A-Z]{2}-\d{2}|\d{2}-\d{2}-[A-Z]{2}|[A-Z]{2}-\d{2}-\d{2})$"),
            "BR" => Regex.IsMatch(p, @"^([A-Z]{3}-?\d{4}|[A-Z]{3}\d[A-Z]\d{2})$"),
            _ => p.Length is >= 2 and <= 15,
        };
    }
}

// Palavra-passe guardada. Generated: gerada pelo Éteris e à espera de confirmação (envio com sucesso). Realm: o
// httpRealm do Firefox (autenticação HTTP, guarda-se e não se preenche na v1).
public sealed record LoginItem(
    string Id, string Origin, string Username, string Password, string? Note,
    DateTimeOffset Created, DateTimeOffset Changed, DateTimeOffset? Used, string? FormAction, bool Generated, string? Realm)
{
    public override string ToString() => $"LoginItem {Id}";
}

// «Nunca neste site».
public sealed record NeverSaveSite(string Origin, DateTimeOffset Created)
{
    public override string ToString() => "NeverSaveSite";
}

// Cartão: titular, número, validade e alcunha. Nunca o código de segurança (VaultFields.IsForbidden).
public sealed record CardItem(string Id, string? Name, string Number, int ExpMonth, int ExpYear, string? Nickname, DateTimeOffset Created, DateTimeOffset? Used)
{
    public string Brand => CardRules.Brand(Number);

    // «Visa •••• 4242 · 08/29 · alcunha».
    public string Summary => $"{Brand} {CardRules.Mask(Number)} · {ExpMonth:00}/{ExpYear % 100:00}" + (string.IsNullOrWhiteSpace(Nickname) ? string.Empty : " · " + Nickname);

    public override string ToString() => $"CardItem {Id}";
}

public sealed record IbanItem(string Id, string Iban, string? Nickname, DateTimeOffset Created)
{
    public string Summary => IbanRules.Mask(Iban) + (string.IsNullOrWhiteSpace(Nickname) ? string.Empty : " · " + Nickname);

    public override string ToString() => $"IbanItem {Id}";
}

// Informações de contacto (morada com campos de Portugal e do Brasil: número, complemento, bairro; distrito/estado).
public sealed record ContactItem(
    string Id, string? Label, string? GivenName, string? FamilyName, string? Organization, string? Street, string? Number,
    string? Complement, string? Neighborhood, string? City, string? Region, string? PostalCode, string? Country, string? Email,
    string? Phone, DateTimeOffset Created, DateTimeOffset? Used)
{
    // «Nome · Rua…, Cidade».
    public string Summary
    {
        get
        {
            var name = Join(" ", GivenName, FamilyName);
            var street = Join(" ", Street, Number);
            var place = Join(", ", street, City);
            return Join(" · ", name.Length > 0 ? name : Organization, place);
        }
    }

    private static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    public override string ToString() => $"ContactItem {Id}";
}

public enum IdDocType { Passport, DriverLicense, NationalId, TaxId }

// Documentos de identificação: passaporte, carta de condução/CNH, Cartão de Cidadão/RG e o número fiscal (NIF/CPF).
public sealed record IdDocItem(
    string Id, IdDocType Type, string Number, string? Name, string? Country, string? Issuer, DateOnly? Issued, DateOnly? Expires, string? Note)
{
    private static readonly (IdDocType Type, string Code)[] Codes =
        [(IdDocType.Passport, "passport"), (IdDocType.DriverLicense, "driverLicense"), (IdDocType.NationalId, "nationalId"), (IdDocType.TaxId, "taxId")];

    public static string TypeCode(IdDocType type) => Codes.First(c => c.Type == type).Code;

    public static IdDocType? ParseType(string? code)
    {
        foreach (var (type, known) in Codes)
            if (string.Equals(known, code, StringComparison.Ordinal)) return type;
        return null;
    }

    // Com o país, o nome desse país; sem ele, os dois (Portugal/Brasil).
    public static string TypeName(IdDocType type, string? country) => (type, country?.ToUpperInvariant()) switch
    {
        (IdDocType.Passport, _) => "Passaporte",
        (IdDocType.DriverLicense, "PT") => "Carta de condução",
        (IdDocType.DriverLicense, "BR") => "CNH",
        (IdDocType.DriverLicense, _) => "Carta de condução/CNH",
        (IdDocType.NationalId, "PT") => "Cartão de Cidadão",
        (IdDocType.NationalId, "BR") => "RG",
        (IdDocType.NationalId, _) => "Cartão de Cidadão/RG",
        (IdDocType.TaxId, "PT") => "NIF",
        (IdDocType.TaxId, "BR") => "CPF",
        _ => "NIF/CPF",
    };

    public string Summary => $"{TypeName(Type, Country)} {VaultFields.MaskTail(Number)}";

    public override string ToString() => $"IdDocItem {Id}";
}

public enum TravelType { Vehicle, Loyalty }

// Viagens: veículos (marca, modelo, ano, proprietário, matrícula/placa, região, VIN) e programas de fidelidade.
public sealed record TravelItem(
    string Id, TravelType Type,
    string? Make, string? Model, int? Year, string? Owner, string? Plate, string? PlateRegion, string? Vin,
    string? Company, string? Program, string? Number, string? Name)
{
    public static TravelItem Vehicle(string id, string? make, string? model, int? year, string? owner, string? plate, string? plateRegion, string? vin)
        => new(id, TravelType.Vehicle, make, model, year, owner, plate, plateRegion, vin, null, null, null, null);

    public static TravelItem Loyalty(string id, string? company, string? program, string? number, string? name)
        => new(id, TravelType.Loyalty, null, null, null, null, null, null, null, company, program, number, name);

    public string Summary => Type == TravelType.Vehicle
        ? string.Join(" · ", new[] { string.Join(' ', new[] { Make, Model }.Where(s => !string.IsNullOrWhiteSpace(s))), Plate ?? string.Empty }.Where(s => s.Length > 0))
        : string.Join(" · ", new[] { Company, Program, string.IsNullOrWhiteSpace(Number) ? null : VaultFields.MaskTail(Number) }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public override string ToString() => $"TravelItem {Id}";
}
