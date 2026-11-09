using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// A1 (docs/palavras-passe.md): modelos do cofre e validações. Só dados de teste inventados (cartões de teste públicos,
// IBAN de exemplo do registo SWIFT, NIF/CPF de exemplo).
public class VaultModelsTests
{
    [Theory]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("4242-4242-4242-4242", true)]
    [InlineData("5555555555554444", true)]
    [InlineData("378282246310005", true)]
    [InlineData("4111 1111 1111 1112", false)]
    [InlineData("4111", false)]
    [InlineData("4111 1111 1111 111a", false)]
    [InlineData("", false)]
    public void CardNumber_IsCheckedByLuhnAndLength(string number, bool valid)
        => Assert.Equal(valid, CardRules.IsValidNumber(number));

    [Theory]
    [InlineData("4111111111111111", "Visa")]
    [InlineData("5555555555554444", "Mastercard")]
    [InlineData("2223003122003222", "Mastercard")]
    [InlineData("378282246310005", "American Express")]
    [InlineData("6011111111111117", "Discover")]
    [InlineData("3530111333300000", "JCB")]
    [InlineData("36227206271667", "Diners Club")]
    [InlineData("6362970000457013", "Elo")]
    [InlineData("6062825624254001", "Hipercard")]
    [InlineData("9999999999999995", "Cartão")]
    public void CardBrand_ComesFromThePrefix(string number, string brand) => Assert.Equal(brand, CardRules.Brand(number));

    [Fact]
    public void CardMask_ShowsOnlyTheLastFour()
    {
        Assert.Equal("•••• 1111", CardRules.Mask("4111 1111 1111 1111"));
        var card = new CardItem("c1", "Ana Teste", "4111111111111111", 8, 2029, "Pessoal", T0, null);
        Assert.Equal("Visa •••• 1111 · 08/29 · Pessoal", card.Summary);
        Assert.DoesNotContain("4111111111111111", card.Summary);
        Assert.DoesNotContain("4111111111111111", card.ToString());
    }

    [Theory]
    [InlineData("08/29", 8, 2029)]
    [InlineData("8/2029", 8, 2029)]
    [InlineData("08-29", 8, 2029)]
    [InlineData("0829", 8, 2029)]
    [InlineData(" 12 / 30 ", 12, 2030)]
    public void Expiry_ParsesTheUsualForms(string text, int month, int year)
        => Assert.Equal((month, year), CardRules.ParseExpiry(text));

    [Theory]
    [InlineData("13/29")]
    [InlineData("00/29")]
    [InlineData("ab/cd")]
    [InlineData("")]
    public void Expiry_RejectsNonsense(string text) => Assert.Null(CardRules.ParseExpiry(text));

    // Um cartão vale até ao fim do mês da validade.
    [Fact]
    public void Expiry_IsValidThroughTheEndOfTheMonth()
    {
        var now = new DateTimeOffset(2029, 8, 31, 23, 0, 0, TimeSpan.Zero);
        Assert.False(CardRules.IsExpired(8, 2029, now));
        Assert.True(CardRules.IsExpired(7, 2029, now));
        Assert.True(CardRules.IsExpired(8, 2029, now.AddHours(2)));
        Assert.False(CardRules.IsExpired(1, 2030, now));
    }

    // O CVV/CVC nunca se guarda: o modelo não tem onde, e um conjunto de campos que o traga é recusado.
    [Fact]
    public void Card_NeverHasASecurityCode()
    {
        foreach (var name in new[] { "cvc", "CVC", "csc", "cvv", "cvv2", "cvc2", "cid", "securityCode", "card_cvv" })
            Assert.True(VaultFields.IsForbidden(name), name);
        Assert.False(VaultFields.IsForbidden("number"));
        Assert.False(VaultFields.IsForbidden("nickname"));
        Assert.False(VaultFields.IsForbidden("city"));
        Assert.DoesNotContain(typeof(CardItem).GetProperties(), p => VaultFields.IsForbidden(p.Name));
        var ex = Assert.Throws<ArgumentException>(() => VaultFields.RejectForbidden(new Dictionary<string, string?> { ["number"] = "4111111111111111", ["cvc"] = "123" }));
        Assert.Contains("cvc", ex.Message);
        Assert.DoesNotContain("123", ex.Message);
        VaultFields.RejectForbidden(new Dictionary<string, string?> { ["number"] = "4111111111111111" });
    }

    [Theory]
    [InlineData("PT50 0002 0123 1234 5678 9015 4", true)]
    [InlineData("pt50000201231234567890154", true)]
    [InlineData("GB82 WEST 1234 5698 7654 32", true)]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    [InlineData("BR18 0036 0305 0000 1000 9795 493C 1", true)]
    [InlineData("PT50 0002 0123 1234 5678 9015 5", false)]
    [InlineData("PT50 0002 0123 1234 5678 9015", false)]
    [InlineData("XX00 1234", false)]
    [InlineData("", false)]
    public void Iban_IsCheckedByMod97AndCountryLength(string iban, bool valid) => Assert.Equal(valid, IbanRules.IsValid(iban));

    [Fact]
    public void Iban_IsNormalisedAndMasked()
    {
        Assert.Equal("PT50000201231234567890154", IbanRules.Normalize(" pt50 0002 0123 1234 5678 9015 4 "));
        Assert.Equal("PT50 •••• 0154", IbanRules.Mask("PT50 0002 0123 1234 5678 9015 4"));
        Assert.Equal("PT50 0002 0123 1234 5678 9015 4", IbanRules.Format("PT50000201231234567890154"));
    }

    // D-2: o número fiscal entra nos documentos — NIF (Portugal) e CPF (Brasil), com o dígito de controlo.
    [Theory]
    [InlineData("123456789", true)]
    [InlineData("123 456 789", true)]
    [InlineData("501964843", true)]
    [InlineData("123456780", false)]
    [InlineData("423456789", false)]
    [InlineData("12345678", false)]
    public void Nif_HasAValidCheckDigit(string nif, bool valid) => Assert.Equal(valid, TaxIdRules.IsValidNif(nif));

    [Theory]
    [InlineData("529.982.247-25", true)]
    [InlineData("52998224725", true)]
    [InlineData("529.982.247-26", false)]
    [InlineData("111.111.111-11", false)]
    [InlineData("5299822472", false)]
    public void Cpf_HasValidCheckDigits(string cpf, bool valid) => Assert.Equal(valid, TaxIdRules.IsValidCpf(cpf));

    [Fact]
    public void TaxId_IsFormattedAndMaskedPerCountry()
    {
        Assert.Equal("529.982.247-25", TaxIdRules.Format("52998224725", "BR"));
        Assert.Equal("123 456 789", TaxIdRules.Format("123456789", "PT"));
        Assert.True(TaxIdRules.IsValid("529.982.247-25", "BR"));
        Assert.True(TaxIdRules.IsValid("123456789", "PT"));
        Assert.False(TaxIdRules.IsValid("123456789", "BR"));
        Assert.True(TaxIdRules.IsValid("qualquer coisa", "ES"));
        var doc = new IdDocItem("d1", IdDocType.TaxId, "52998224725", "Ana Teste", "BR", null, null, null, null);
        Assert.Equal("CPF •••• 4725", doc.Summary);
        Assert.Equal("NIF •••• 6789", (doc with { Number = "123456789", Country = "PT" }).Summary);
        Assert.DoesNotContain("52998224725", doc.ToString());
    }

    // Os nomes dos tipos aparecem os dois quando o país não está dito (carta de condução/CNH, Cartão de Cidadão/RG).
    [Fact]
    public void IdDocTypes_HaveTheNamesOfBothCountries()
    {
        Assert.Equal("Passaporte", IdDocItem.TypeName(IdDocType.Passport, null));
        Assert.Equal("Carta de condução/CNH", IdDocItem.TypeName(IdDocType.DriverLicense, null));
        Assert.Equal("Carta de condução", IdDocItem.TypeName(IdDocType.DriverLicense, "PT"));
        Assert.Equal("CNH", IdDocItem.TypeName(IdDocType.DriverLicense, "BR"));
        Assert.Equal("Cartão de Cidadão/RG", IdDocItem.TypeName(IdDocType.NationalId, null));
        Assert.Equal("Cartão de Cidadão", IdDocItem.TypeName(IdDocType.NationalId, "PT"));
        Assert.Equal("RG", IdDocItem.TypeName(IdDocType.NationalId, "BR"));
        Assert.Equal("NIF/CPF", IdDocItem.TypeName(IdDocType.TaxId, null));
        Assert.Equal("passport", IdDocItem.TypeCode(IdDocType.Passport));
        Assert.Equal("driverLicense", IdDocItem.TypeCode(IdDocType.DriverLicense));
        Assert.Equal("nationalId", IdDocItem.TypeCode(IdDocType.NationalId));
        Assert.Equal("taxId", IdDocItem.TypeCode(IdDocType.TaxId));
        Assert.Equal(IdDocType.TaxId, IdDocItem.ParseType("taxId"));
        Assert.Null(IdDocItem.ParseType("ktn"));
    }

    [Theory]
    [InlineData("1000-001", "PT", true)]
    [InlineData("1000001", "PT", false)]
    [InlineData("01310-100", "BR", true)]
    [InlineData("01310100", "BR", true)]
    [InlineData("0131-100", "BR", false)]
    [InlineData("SW1A 1AA", "GB", true)]
    public void PostalCode_PerCountry(string code, string country, bool valid) => Assert.Equal(valid, ContactRules.IsValidPostalCode(code, country));

    [Fact]
    public void PostalCode_IsNormalised()
    {
        Assert.Equal("01310-100", ContactRules.NormalizePostalCode("01310100", "BR"));
        Assert.Equal("1000-001", ContactRules.NormalizePostalCode(" 1000-001 ", "PT"));
    }

    [Theory]
    [InlineData("ana@exemplo.test", true)]
    [InlineData("ana.teste+x@sub.exemplo.pt", true)]
    [InlineData("ana@", false)]
    [InlineData("ana exemplo.test", false)]
    public void Email_LightCheck(string email, bool valid) => Assert.Equal(valid, ContactRules.IsValidEmail(email));

    [Theory]
    [InlineData("AA-00-AA", "PT", true)]
    [InlineData("00-AA-00", "PT", true)]
    [InlineData("ABC1D23", "BR", true)]
    [InlineData("ABC-1234", "BR", true)]
    [InlineData("AB-12", "PT", false)]
    public void Plate_PerCountry(string plate, string country, bool valid) => Assert.Equal(valid, TravelRules.IsValidPlate(plate, country));

    [Fact]
    public void Summaries_NeverCarryTheSecret()
    {
        var login = new LoginItem("l1", "https://conta.exemplo.test", "ana@exemplo.test", "Inventada-123!", "nota secreta", T0, T0, null, null, false, null);
        Assert.DoesNotContain("Inventada-123!", login.ToString());
        Assert.DoesNotContain("nota secreta", login.ToString());
        var iban = new IbanItem("i1", "PT50000201231234567890154", "Conta", T0);
        Assert.Equal("PT50 •••• 0154 · Conta", iban.Summary);
        Assert.DoesNotContain("PT50000201231234567890154", iban.ToString());
        var contact = new ContactItem("k1", "Casa", "Ana", "Teste", null, "Rua do Exemplo", "1", null, null, "Lisboa", null, "1000-001", "PT", "ana@exemplo.test", "+351 210 000 000", T0, null);
        Assert.Equal("Ana Teste · Rua do Exemplo 1, Lisboa", contact.Summary);
        var car = TravelItem.Vehicle("t1", "Marca", "Modelo", 2020, "Ana Teste", "AA-00-AA", "PT", null);
        Assert.Equal("Marca Modelo · AA-00-AA", car.Summary);
        var loyalty = TravelItem.Loyalty("t2", "Companhia", "Programa", "123456789", "Ana Teste");
        Assert.Equal("Companhia · Programa · •••• 6789", loyalty.Summary);
        Assert.DoesNotContain("123456789", loyalty.ToString());
    }

    [Fact]
    public void Collections_HaveTheSyncNames()
    {
        Assert.Equal("logins", VaultCollections.Name(VaultCollection.Logins));
        Assert.Equal("loginsNever", VaultCollections.Name(VaultCollection.LoginsNever));
        Assert.Equal("cards", VaultCollections.Name(VaultCollection.Cards));
        Assert.Equal("ibans", VaultCollections.Name(VaultCollection.Ibans));
        Assert.Equal("contacts", VaultCollections.Name(VaultCollection.Contacts));
        Assert.Equal("idDocs", VaultCollections.Name(VaultCollection.IdDocs));
        Assert.Equal("travel", VaultCollections.Name(VaultCollection.Travel));
        Assert.Equal(VaultCollection.IdDocs, VaultCollections.Parse("idDocs"));
        Assert.Null(VaultCollections.Parse("bookmarks"));
    }

    // Um item novo leva um ID aleatório opaco (nada do site nem do utilizador).
    [Fact]
    public void NewIds_AreRandomAndOpaque()
    {
        var a = VaultIds.New();
        var b = VaultIds.New();
        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{32}$", a);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
}
