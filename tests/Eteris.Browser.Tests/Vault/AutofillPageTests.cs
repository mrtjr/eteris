using Eteris.Browser.Browser;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// B3 (docs/palavras-passe.md): eteris://autofill/ — Pagamentos, Informações de contacto, Documentos de identificação e
// Viagens. Números mascarados na página, o número completo só com a reautenticação, formulários de morada de Portugal e
// do Brasil, validações e nunca o código de segurança do cartão.
public sealed class AutofillPageTests : IDisposable
{
    private readonly VaultFixture _f = new();

    public void Dispose() => _f.Dispose();

    private const string Card = "4111 1111 1111 1111";
    private const string Iban = "PT50000201231234567890154";
    private const string Nif = "123456789";
    private const string Cpf = "52998224725";
    private const string Vin = "1HGCM82633A004352";
    private const string Loyalty = "TP987654321";

    private Dictionary<string, string> CardForm(string number = Card, string exp = "08/29") => new() { ["name"] = "Ana Teste", ["number"] = number, ["exp"] = exp, ["nickname"] = "Pessoal" };

    private async Task<string> AddCard()
        => VaultFixture.Json(await _f.Post("autofill", "/save", CardForm(), "c=cards"))["id"]!.GetValue<string>();

    // Nenhum número completo no HTML inicial: cartões, IBAN, documentos e números de viagem só mascarados.
    [Fact]
    public async Task Page_OnlyMaskedNumbers()
    {
        await AddCard();
        _f.Vault.Save(new IbanItem(string.Empty, Iban, "Conta", default));
        _f.Vault.Save(new IdDocItem(string.Empty, IdDocType.TaxId, Nif, "Ana Teste", "PT", null, null, null, null));
        _f.Vault.Save(TravelItem.Vehicle(string.Empty, "Renault", "Clio", 2019, "Ana Teste", "AB-12-CD", "PT", Vin));
        _f.Vault.Save(TravelItem.Loyalty(string.Empty, "TAP", "Miles&Go", Loyalty, "Ana Teste"));
        var html = (await _f.Get("autofill", "/")).Body;
        foreach (var secret in new[] { "4111111111111111", Card, Iban, "PT50 0002 0123", Nif, "123 456 789", Vin, Loyalty })
            Assert.DoesNotContain(secret, html);
        Assert.Contains("•••• 1111", html);
        Assert.Contains("PT50 •••• 0154", html);
        Assert.Contains("•••• 6789", html);
        Assert.Contains("•••• 4321", html);
        Assert.Contains("Visa", html);
        Assert.Contains("AB-12-CD", html);
        Assert.Equal(0, _f.Reauth.Prompts);
    }

    [Fact]
    public void Template_SectionsThemedNoSecurityCode()
    {
        var html = AutofillPage.Template;
        foreach (var id in new[] { "pagamentos", "contacto", "documentos", "viagens" }) Assert.Contains($"id=\"{id}\"", html);
        Assert.Contains("html.light {", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("cvc", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cvv", html, StringComparison.OrdinalIgnoreCase);
    }

    // Os documentos com os nomes dos dois países: carta de condução/CNH, Cartão de Cidadão/RG, NIF/CPF (D-2).
    [Fact]
    public async Task Page_DocumentNamesInBothForms()
    {
        var html = (await _f.Get("autofill", "/")).Body;
        foreach (var name in new[] { "Passaporte", "Carta de condução/CNH", "Cartão de Cidadão/RG", "NIF/CPF" }) Assert.Contains(name, html);
    }

    // O número completo só com o Windows Hello (cartões, IBAN, documentos, viagens); os contactos não têm segredos.
    [Theory]
    [InlineData("cards")]
    [InlineData("ibans")]
    [InlineData("idDocs")]
    [InlineData("travel")]
    public async Task Reveal_NeedsReauth(string collection)
    {
        var id = collection switch
        {
            "cards" => await AddCard(),
            "ibans" => _f.Vault.Save(new IbanItem(string.Empty, Iban, null, default)),
            "idDocs" => _f.Vault.Save(new IdDocItem(string.Empty, IdDocType.TaxId, Nif, null, "PT", null, null, null, "nota-doc")),
            _ => _f.Vault.Save(TravelItem.Loyalty(string.Empty, "TAP", "Miles&Go", Loyalty, null)),
        };
        var secret = collection switch { "cards" => "4111 1111 1111 1111", "ibans" => "PT50 0002 0123 1234 5678 9015 4", "idDocs" => "123 456 789", _ => Loyalty };
        _f.Reauth.Outcome = ReauthOutcome.Failed;
        var refused = await _f.Get("autofill", "/reveal", $"c={collection}&id={id}");
        Assert.False(VaultFixture.Json(refused)["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(secret, refused.Body);
        _f.Reauth.Outcome = ReauthOutcome.Verified;
        var shown = await _f.Get("autofill", "/reveal", $"c={collection}&id={id}");
        Assert.True(VaultFixture.Json(shown)["ok"]!.GetValue<bool>());
        Assert.Contains(secret, VaultFixture.Json(shown)["item"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal(1, _f.Host.Shown);
        Assert.Equal(2, _f.Reauth.Prompts);
    }

    // Nunca o código de segurança: um formulário com «cvc» (ou «cvv», «csc», «security code») é recusado sem guardar.
    [Theory]
    [InlineData("cvc")]
    [InlineData("cvv")]
    [InlineData("csc")]
    [InlineData("card-security-code")]
    public async Task Save_SecurityCodeRefused(string field)
    {
        var form = CardForm();
        form[field] = "123";
        var reply = VaultFixture.Json(await _f.Post("autofill", "/save", form, "c=cards"));
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("código de segurança", reply["message"]!.GetValue<string>());
        Assert.Empty(_f.Vault.List(VaultCollection.Cards));
    }

    [Theory]
    [InlineData("cards", "number=4111 1111 1111 1112&exp=08/29", "Número de cartão inválido")]
    [InlineData("cards", "number=4111 1111 1111 1111&exp=13/29", "Validade")]
    [InlineData("cards", "number=4111 1111 1111 1111&exp=", "Validade")]
    [InlineData("ibans", "iban=PT50000201231234567890155", "IBAN inválido")]
    [InlineData("contacts", "country=PT&postalCode=1000&givenName=Ana", "Código postal inválido")]
    [InlineData("contacts", "country=BR&postalCode=0131&givenName=Ana", "CEP inválido")]
    [InlineData("contacts", "country=PT&email=ana-sem-arroba&givenName=Ana", "E-mail inválido")]
    [InlineData("contacts", "country=PT", "Preencha")]
    [InlineData("idDocs", "type=taxId&country=PT&number=123456780", "NIF inválido")]
    [InlineData("idDocs", "type=taxId&country=BR&number=52998224726", "CPF inválido")]
    [InlineData("idDocs", "type=passport&country=PT&number=", "número")]
    [InlineData("idDocs", "type=passport&country=PT&number=X1&expires=31-12-2030", "Data")]
    [InlineData("travel", "type=vehicle&plateRegion=PT&plate=AB-123-CD&make=Renault", "Matrícula inválida")]
    [InlineData("travel", "type=vehicle&plateRegion=BR&plate=AB1234&make=Fiat", "Placa inválida")]
    [InlineData("travel", "type=vehicle&year=19&make=Fiat", "Ano")]
    [InlineData("travel", "type=loyalty", "Preencha")]
    public async Task Save_Validates(string collection, string form, string expected)
    {
        var fields = form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var reply = VaultFixture.Json(await _f.Post("autofill", "/save", fields, "c=" + collection));
        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains(expected, reply["message"]!.GetValue<string>());
        Assert.Empty(_f.Vault.List(VaultCollection.Cards).Concat(_f.Vault.List(VaultCollection.Ibans)).Concat(_f.Vault.List(VaultCollection.Contacts))
            .Concat(_f.Vault.List(VaultCollection.IdDocs)).Concat(_f.Vault.List(VaultCollection.Travel)));
    }

    // Um de cada: adicionar não pede nada e guarda já normalizado (CEP com hífen, NIF/CPF, matrícula, IBAN).
    [Fact]
    public async Task Save_OneOfEach()
    {
        async Task<string> Save(string c, Dictionary<string, string> form)
        {
            var reply = VaultFixture.Json(await _f.Post("autofill", "/save", form, "c=" + c));
            Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
            return reply["id"]!.GetValue<string>();
        }
        var card = await Save("cards", CardForm("5555 5555 5555 4444", "1/2031"));
        var iban = await Save("ibans", new() { ["iban"] = "pt50 0002 0123 1234 5678 9015 4", ["nickname"] = "Casa" });
        var pt = await Save("contacts", new() { ["label"] = "Casa", ["givenName"] = "Ana", ["familyName"] = "Teste", ["street"] = "Rua Inventada", ["number"] = "12", ["city"] = "Lisboa", ["region"] = "Lisboa", ["postalCode"] = "1000-001", ["country"] = "PT", ["email"] = "ana.teste@exemplo.pt", ["phone"] = "+351 210 000 000" });
        var br = await Save("contacts", new() { ["label"] = "Trabalho", ["givenName"] = "Bruno", ["street"] = "Avenida Inventada", ["number"] = "1000", ["complement"] = "Sala 5", ["neighborhood"] = "Bela Vista", ["city"] = "São Paulo", ["region"] = "SP", ["postalCode"] = "01310100", ["country"] = "BR" });
        var nif = await Save("idDocs", new() { ["type"] = "taxId", ["country"] = "PT", ["number"] = "123 456 789", ["name"] = "Ana Teste" });
        var cpf = await Save("idDocs", new() { ["type"] = "taxId", ["country"] = "BR", ["number"] = "529.982.247-25" });
        var cnh = await Save("idDocs", new() { ["type"] = "driverLicense", ["country"] = "BR", ["number"] = "01234567890", ["expires"] = "2031-05-01" });
        var car = await Save("travel", new() { ["type"] = "vehicle", ["make"] = "Fiat", ["model"] = "Uno", ["year"] = "2015", ["plate"] = "abc1d23", ["plateRegion"] = "BR", ["vin"] = Vin });
        var miles = await Save("travel", new() { ["type"] = "loyalty", ["company"] = "TAP", ["program"] = "Miles&Go", ["number"] = Loyalty, ["name"] = "Ana Teste" });
        Assert.Equal(0, _f.Reauth.Prompts);
        Assert.Equal("5555555555554444", _f.Vault.GetCard(card)!.Number);
        Assert.Equal((1, 2031), (_f.Vault.GetCard(card)!.ExpMonth, _f.Vault.GetCard(card)!.ExpYear));
        Assert.Equal(Iban, _f.Vault.GetIban(iban)!.Iban);
        Assert.Equal("1000-001", _f.Vault.GetContact(pt)!.PostalCode);
        Assert.Equal("01310-100", _f.Vault.GetContact(br)!.PostalCode);
        Assert.Equal("Bela Vista", _f.Vault.GetContact(br)!.Neighborhood);
        Assert.Equal(Nif, _f.Vault.GetIdDoc(nif)!.Number);
        Assert.Equal(Cpf, _f.Vault.GetIdDoc(cpf)!.Number);
        Assert.Equal(new DateOnly(2031, 5, 1), _f.Vault.GetIdDoc(cnh)!.Expires);
        Assert.Equal("ABC1D23", _f.Vault.GetTravel(car)!.Plate);
        Assert.Equal(Vin, _f.Vault.GetTravel(car)!.Vin);
        Assert.Equal(Loyalty, _f.Vault.GetTravel(miles)!.Number);
        var html = (await _f.Get("autofill", "/")).Body;
        Assert.Contains("Mastercard", html);
        Assert.Contains("CNH", html);
        Assert.Contains("CPF", html);
        Assert.Contains("NIF", html);
    }

    // Editar um item com segredo pede o Hello (é ver o número); sem ele nada muda. Os contactos editam-se sem pedir.
    [Fact]
    public async Task Edit_SecretItemNeedsReauth_ContactDoesNot()
    {
        var card = await AddCard();
        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        var form = CardForm("5555 5555 5555 4444");
        form["id"] = card;
        Assert.False(VaultFixture.Json(await _f.Post("autofill", "/save", form, "c=cards"))["ok"]!.GetValue<bool>());
        Assert.Equal("4111111111111111", _f.Vault.GetCard(card)!.Number);
        _f.Reauth.Outcome = ReauthOutcome.Verified;
        Assert.True(VaultFixture.Json(await _f.Post("autofill", "/save", form, "c=cards"))["ok"]!.GetValue<bool>());
        Assert.Equal("5555555555554444", _f.Vault.GetCard(card)!.Number);
        Assert.Single(_f.Vault.List(VaultCollection.Cards));

        var prompts = _f.Reauth.Prompts;
        var contact = _f.Vault.Save(new ContactItem(string.Empty, "Casa", "Ana", null, null, null, null, null, null, "Porto", null, null, "PT", null, null, default, null));
        var edited = VaultFixture.Json(await _f.Post("autofill", "/save", new() { ["id"] = contact, ["givenName"] = "Ana Maria", ["city"] = "Porto", ["country"] = "PT" }, "c=contacts"));
        Assert.True(edited["ok"]!.GetValue<bool>());
        Assert.Equal("Ana Maria", _f.Vault.GetContact(contact)!.GivenName);
        Assert.Equal(prompts, _f.Reauth.Prompts);
    }

    // Apagar com «Anular», como nas palavras-passe.
    [Fact]
    public async Task Delete_WithUndo()
    {
        var card = await AddCard();
        var reply = VaultFixture.Json(await _f.Get("autofill", "/delete", $"c=cards&id={card}"));
        Assert.DoesNotContain("•••• 1111", (await _f.Get("autofill", "/")).Body);
        await _f.Get("autofill", "/undo", "u=" + reply["undo"]!.GetValue<string>());
        Assert.Contains("•••• 1111", (await _f.Get("autofill", "/")).Body);
        await _f.Get("autofill", "/delete", $"c=cards&id={card}");
        _f.Host.RunScheduled();
        Assert.Null(_f.Vault.GetCard(card));
    }

    // «Pedir o Windows Hello ao preencher pagamentos»: ligar não pede; desligar pede (e, recusado, fica ligado).
    [Fact]
    public async Task PaymentsHelloSwitch_TurningOffNeedsReauth()
    {
        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        Assert.Equal("recusado", (await _f.Get("autofill", "/set", "k=payAskHello&v=false")).Body);
        Assert.True(_f.Host.Current.PaymentsAskHello);
        Assert.Equal(ReauthPolicy.Message(ReauthPurpose.ChangePaymentSetting), _f.Reauth.Messages.Single());
        _f.Reauth.Outcome = ReauthOutcome.Verified;
        Assert.Equal("ok", (await _f.Get("autofill", "/set", "k=payAskHello&v=false")).Body);
        Assert.False(_f.Host.Current.PaymentsAskHello);
        Assert.Equal("ok", (await _f.Get("autofill", "/set", "k=payAskHello&v=true")).Body);
        Assert.Equal(2, _f.Reauth.Prompts);
        Assert.Equal("ok", (await _f.Get("autofill", "/set", "k=afContacts&v=false")).Body);
        Assert.False(_f.Host.Current.AutofillContacts);
        Assert.Equal("recusado", (await _f.Get("autofill", "/set", "k=pwOffer&v=false")).Body);
    }

    // Auditoria (VAU-07): outra grafia de «false» não desliga o «Pedir o Windows Hello» dos pagamentos.
    [Theory] [InlineData("False")] [InlineData("FALSE")] [InlineData("false%20")]
    public async Task PaymentsHelloSwitch_TurningOff_IsRefusedWhateverTheSpelling(string v)
    {
        _f.Reauth.Outcome = ReauthOutcome.Canceled;
        Assert.Equal("recusado", (await _f.Get("autofill", "/set", "k=payAskHello&v=" + v)).Body);
        Assert.True(_f.Host.Current.PaymentsAskHello);
    }

    // Formulários de morada por país: Portugal (código postal, distrito, sem bairro) e Brasil (CEP, bairro, estado).
    [Fact]
    public void AddressForms_PortugalAndBrazil()
    {
        var pt = AutofillPage.AddressForm("PT").ToDictionary(f => f.Key, f => f.Label);
        var br = AutofillPage.AddressForm("BR").ToDictionary(f => f.Key, f => f.Label);
        var other = AutofillPage.AddressForm(null).ToDictionary(f => f.Key, f => f.Label);
        Assert.Equal("Código postal", pt["postalCode"]);
        Assert.Equal("Distrito", pt["region"]);
        Assert.DoesNotContain("neighborhood", pt.Keys);
        Assert.Equal("CEP", br["postalCode"]);
        Assert.Equal("Estado", br["region"]);
        Assert.Equal("Bairro", br["neighborhood"]);
        Assert.Equal("Complemento", br["complement"]);
        Assert.Equal("Código postal", other["postalCode"]);
        Assert.Equal("Região", other["region"]);
        Assert.Contains("1000-001", AutofillPage.AddressForm("PT").Single(f => f.Key == "postalCode").Placeholder);
        Assert.Contains("00000-000", AutofillPage.AddressForm("BR").Single(f => f.Key == "postalCode").Placeholder);
    }

    // Cartão fora da validade marcado; nada de segredos nos dados da lista.
    [Fact]
    public async Task List_ExpiredCardMarked()
    {
        await _f.Post("autofill", "/save", CardForm(exp: "01/20"), "c=cards");
        var list = VaultFixture.Json(await _f.Get("autofill", "/list"));
        var card = list["cards"]!.AsArray().Single()!;
        Assert.True(card["expired"]!.GetValue<bool>());
        Assert.Equal("01/20", card["exp"]!.GetValue<string>());
    }

    [Fact]
    public async Task PrivateAndToken()
    {
        var card = await AddCard();
        Assert.Contains(VaultRoutes.PrivateMessage, (await _f.Get("autofill", "/", isPrivate: true)).Body);
        Assert.Equal("recusado", (await _f.Get("autofill", "/reveal", $"c=cards&id={card}", isPrivate: true)).Body);
        Assert.Equal("recusado", (await _f.Get("autofill", "/reveal", $"c=cards&id={card}", authorized: false)).Body);
        Assert.Equal("recusado", (await _f.Post("autofill", "/save", CardForm(), "c=cards", authorized: false)).Body);
        Assert.Equal(0, _f.Reauth.Prompts);
    }

    // «Entrar com o Google» (0.2.15, T13): o botão só sem sessão, e a rota só com o token.
    [Fact]
    public async Task Page_OffersSignIn_OnlyWithoutSession()
    {
        _f.Host.SignInOffer = true;
        Assert.Contains("\"signIn\":true", (await _f.Get("autofill", "/")).Body);
        _f.Host.SignInOffer = false;
        Assert.Contains("\"signIn\":false", (await _f.Get("autofill", "/")).Body);
    }

    [Fact]
    public async Task SignIn_NeedsTheToken()
    {
        _f.Host.SignInOffer = true;
        Assert.Equal(VaultReply.Refused, await _f.Get("autofill", "/sign-in", authorized: false));
        Assert.Equal(0, _f.Host.SignIns);
        Assert.True(VaultFixture.Json(await _f.Get("autofill", "/sign-in"))["ok"]!.GetValue<bool>());
        Assert.Equal(1, _f.Host.SignIns);
    }
}
