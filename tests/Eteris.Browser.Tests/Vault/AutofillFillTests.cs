using Eteris.Browser.Browser;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C6 (docs/palavras-passe.md): preencher contactos, pagamentos, documentos e viagens. A lista aparece só no tipo de campo
// e de formulário certos (com o interruptor da secção ligado); cartões, IBAN e documentos nunca em http e sempre com a
// reautenticação (D-5); o número fiscal (NIF/CPF) deteta-se sozinho; documentos e viagens, só pelo menu de contexto.
public class AutofillFillTests
{
    private static readonly AutofillSwitches AllOn = new(Contacts: true, Payments: true, Documents: true);

    // ---- Quando aparece a lista ----

    [Theory]
    [InlineData("given-name", "address", AutofillMode.Contacts)]
    [InlineData("postal-code", "address", AutofillMode.Contacts)]
    [InlineData("street-address", "payment", AutofillMode.Contacts)]   // checkout com a morada no formulário do cartão
    [InlineData("email", "other", AutofillMode.None)]                   // um e-mail solto (newsletter) não é uma morada
    [InlineData("cc-number", "payment", AutofillMode.Cards)]
    [InlineData("cc-name", "payment", AutofillMode.Cards)]
    [InlineData("cc-exp", "payment", AutofillMode.Cards)]
    [InlineData("cc-csc", "payment", AutofillMode.None)]                // o CVC nunca
    [InlineData("iban", "other", AutofillMode.Ibans)]
    [InlineData("tax-id", "address", AutofillMode.TaxIds)]              // o NIF/CPF deteta-se sozinho (D-2)
    [InlineData("tax-id", "other", AutofillMode.TaxIds)]
    [InlineData("id-number", "other", AutofillMode.None)]               // documentos: só pelo menu de contexto
    [InlineData("plate", "other", AutofillMode.None)]                   // viagens: só pelo menu de contexto
    [InlineData("username", "login", AutofillMode.None)]                // das palavras-passe (C3)
    public void Mode_DependsOnTheFieldAndTheForm(string kind, string formKind, AutofillMode expected)
        => Assert.Equal(expected, AutofillPolicy.ModeFor(kind, formKind, AllOn));

    [Fact]
    public void SectionSwitches_TurnTheirListsOff()
    {
        Assert.Equal(AutofillMode.None, AutofillPolicy.ModeFor("postal-code", "address", AllOn with { Contacts = false }));
        Assert.Equal(AutofillMode.None, AutofillPolicy.ModeFor("cc-number", "payment", AllOn with { Payments = false }));
        Assert.Equal(AutofillMode.None, AutofillPolicy.ModeFor("iban", "other", AllOn with { Payments = false }));
        Assert.Equal(AutofillMode.None, AutofillPolicy.ModeFor("tax-id", "other", AllOn with { Documents = false }));
    }

    // Cartões, IBAN e documentos nunca em http (o próprio computador conta como seguro); contactos e viagens sim.
    [Theory]
    [InlineData(FillCategory.Card, "http://loja.exemplo.test", false)]
    [InlineData(FillCategory.Iban, "http://loja.exemplo.test", false)]
    [InlineData(FillCategory.Document, "http://loja.exemplo.test", false)]
    [InlineData(FillCategory.Card, "https://loja.exemplo.test", true)]
    [InlineData(FillCategory.Document, "http://127.0.0.1:8902", true)]
    [InlineData(FillCategory.Contact, "http://loja.exemplo.test", true)]
    [InlineData(FillCategory.Travel, "http://loja.exemplo.test", true)]
    public void SecretCategories_NeverOnHttp(FillCategory category, string origin, bool allowed)
        => Assert.Equal(allowed, AutofillPolicy.AllowedOn(category, origin));

    // Sempre com a reautenticação (o interruptor «Pedir o Windows Hello ao preencher pagamentos», ligado por omissão).
    [Fact]
    public void SecretCategories_AskWindows()
    {
        Assert.Equal(ReauthPurpose.FillPayment, AutofillPolicy.Reauth(FillCategory.Card));
        Assert.Equal(ReauthPurpose.FillPayment, AutofillPolicy.Reauth(FillCategory.Iban));
        Assert.Equal(ReauthPurpose.FillDocument, AutofillPolicy.Reauth(FillCategory.Document));
        Assert.Null(AutofillPolicy.Reauth(FillCategory.Contact));
        Assert.Null(AutofillPolicy.Reauth(FillCategory.Travel));
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.FillPayment, askPasswords: false, askPayments: new Data.UserSettings().PaymentsAskHello));
        Assert.True(ReauthPolicy.Needs(ReauthPurpose.FillDocument, askPasswords: false, askPayments: new Data.UserSettings().PaymentsAskHello));
    }

    // ---- O que se preenche ----

    private static readonly ContactItem BrContact = new("c1", "Casa", "Ana", "Teste", null, "Avenida Inventada", "1000", "Apto 12", "Centro",
        "São Paulo", "SP", "01310-100", "BR", "ana@exemplo.test", "+55 11 90000-0000", default, null);

    [Fact]
    public void Contact_Values_ByFieldKind()
    {
        var v = AutofillValues.Contact(BrContact);
        Assert.Equal("Ana", v["given-name"]);
        Assert.Equal("Teste", v["family-name"]);
        Assert.Equal("Ana Teste", v["name"]);
        Assert.Equal("Avenida Inventada", v["street-address"]);
        Assert.Equal("1000", v["address-number"]);
        Assert.Equal("Apto 12", v["address-complement"]);
        Assert.Equal("Centro", v["neighborhood"]);
        Assert.Equal("01310-100", v["postal-code"]);
        Assert.Equal("Brasil", v["country"]);
        Assert.Equal("BR", v["country:alt"]);
        Assert.Equal("+55 11 90000-0000", v["tel"]);
    }

    // O cartão: número, titular e validade; nunca o código de segurança.
    [Fact]
    public void Card_Values_NeverTheSecurityCode()
    {
        var v = AutofillValues.Card(new CardItem("k1", "Titular Inventado", "4111111111111111", 8, 2029, null, default, null));
        Assert.Equal("4111111111111111", v["cc-number"]);
        Assert.Equal("Titular Inventado", v["cc-name"]);
        Assert.Equal("08", v["cc-exp-month"]);
        Assert.Equal("2029", v["cc-exp-year"]);
        Assert.Equal("08/29", v["cc-exp"]);
        Assert.DoesNotContain(v.Keys, k => VaultFields.IsForbidden(k) || k.Contains("csc", StringComparison.Ordinal));
    }

    [Fact]
    public void TaxId_IsFormattedForTheCountry()
    {
        Assert.Equal("529.982.247-25", AutofillValues.TaxId(new IdDocItem("d1", IdDocType.TaxId, "52998224725", null, "BR", null, null, null, null))["tax-id"]);
        Assert.Equal("123 456 789", AutofillValues.TaxId(new IdDocItem("d2", IdDocType.TaxId, "123456789", null, "PT", null, null, null, null))["tax-id"]);
    }

    // Pelo menu de contexto num campo que não é do tipo da categoria: só esse campo, com o valor principal do item.
    [Fact]
    public void Single_Values_ForTheClickedField()
    {
        Assert.Equal("AA-00-AA", AutofillValues.Single(TravelItem.Vehicle("t1", "Marca", "Modelo", 2020, null, "AA-00-AA", "PT", "VIN-1")));
        Assert.Equal("NUM-123", AutofillValues.Single(TravelItem.Loyalty("t2", "Companhia", "Programa", "NUM-123", null)));
        Assert.Equal("P1234567", AutofillValues.Single(new IdDocItem("d3", IdDocType.Passport, "P1234567", null, "PT", null, null, null, null)));
        Assert.Equal("4111111111111111", AutofillValues.Single(new CardItem("k1", null, "4111111111111111", 8, 2029, null, default, null)));
    }

    // As linhas da lista: mascaradas, nunca o número inteiro.
    [Fact]
    public void Rows_AreMasked()
    {
        var card = AutofillRows.Card(new CardItem("k1", "Titular", "4111111111111111", 8, 2029, "Cartão de teste", default, null));
        Assert.Equal("Visa •••• 1111", card.Title);
        Assert.Equal("08/29 · Cartão de teste", card.Detail);
        var doc = AutofillRows.Document(new IdDocItem("d1", IdDocType.TaxId, "52998224725", "Titular", "BR", null, null, null, null));
        Assert.DoesNotContain("529982", doc.Title + doc.Detail);
        Assert.StartsWith("CPF", doc.Title);
        var contact = AutofillRows.Contact(BrContact);
        Assert.Equal("Ana Teste", contact.Title);
        Assert.Equal("Avenida Inventada 1000, São Paulo", contact.Detail);
    }

    // «Preencher com…» no menu de contexto: em qualquer campo de uma página onde o cofre está.
    [Fact]
    public void ContextMenu_OffersFillWith_OnVaultFields()
    {
        static bool Has(PageContext c) => PageMenu.Build(c, "x").Any(i => i?.Command == PageCommand.FillWith);
        var field = new PageContext(null, null, "", IsEditable: true, false, false, false) { Vault = new VaultFieldInfo(false, true) };
        Assert.True(Has(field));
        Assert.True(Has(field with { IsPrivate = true }));   // a janela privada preenche a pedido (D-3)
        Assert.False(Has(field with { Vault = null }));
        Assert.Equal("Preencher com…", PageMenu.Build(field, "x").First(i => i?.Command == PageCommand.FillWith)!.Label);
    }

    // ---- No script (Node): o formulário inteiro, com os formatos de cada campo ----

    [Fact]
    public void Script_FillsTheAddress_ComposingWhatTheFormNeeds()
    {
        var output = CofreClassifierTests.Run("""
            const v = { 'given-name': 'Ana', 'family-name': 'Teste', 'street-address': 'Rua X', 'address-number': '12', 'postal-code': '1000-001', 'city': 'Lisboa' };
            // Sem campo para o número: vai com a rua. Nome completo num só campo.
            const a = C.fillPlan(['name', 'street-address', 'postal-code', 'city', 'cc-csc'], [true, true, true, true, true], v);
            // Com campo para o número: separado.
            const b = C.fillPlan(['given-name', 'street-address', 'address-number'], [true, true, true], v);
            print(JSON.stringify(a) + ' ' + JSON.stringify(b));
            """);
        Assert.Equal("""[[0,"Ana Teste"],[1,"Rua X, 12"],[2,"1000-001"],[3,"Lisboa"]] [[0,"Ana"],[1,"Rua X"],[2,"12"]]""", output);
    }

    [Fact]
    public void Script_FillsTheCard_InTheFormatOfEachField_AndNeverTheCvc()
    {
        var output = CofreClassifierTests.Run("""
            const v = { 'cc-number': '4111111111111111', 'cc-name': 'Titular', 'cc-exp-month': '08', 'cc-exp-year': '2029', 'cc-exp': '08/29', 'cc-csc': '123' };
            const kinds = ['cc-number', 'cc-name', 'cc-exp', 'cc-csc', 'cc-exp', 'cc-exp-month', 'cc-exp-year', 'cc-exp-year'];
            const metas = [{}, {}, { maxLength: 5 }, {}, { placeholder: 'MM/AAAA' }, { options: [{ value: '', text: 'Mês' }, { value: '8', text: 'Agosto' }] },
              { options: [{ value: '28', text: '2028' }, { value: '29', text: '2029' }] }, { maxLength: 2 }];
            print(JSON.stringify(C.fillPlan(kinds, kinds.map(() => true), v, metas)) + ' foco=' + C.focusAfter(kinds, kinds.map(() => true), v, 0));
            """);
        Assert.Equal("""[[0,"4111111111111111"],[1,"Titular"],[2,"08/29"],[4,"08/2029"],[5,"8"],[6,"29"],[7,"29"]] foco=3""", output);
    }

    // Países e estados em listas (pelo nome ou pelo código); campos curtos recebem só os algarismos (CPF, CEP).
    [Fact]
    public void Script_PicksOptions_AndFitsShortFields()
    {
        var output = CofreClassifierTests.Run("""
            const v = { 'country': 'Brasil', 'country:alt': 'BR', 'region': 'SP', 'tax-id': '529.982.247-25', 'postal-code': '01310-100' };
            const kinds = ['country', 'region', 'tax-id', 'postal-code', 'tax-id'];
            const metas = [{ options: [{ value: 'PT', text: 'Portugal' }, { value: 'BR', text: 'Brasil' }] }, { options: [{ value: 'RJ', text: 'Rio de Janeiro' }, { value: 'SP', text: 'São Paulo' }] },
              { maxLength: 11 }, { maxLength: 8 }, { maxLength: 14 }];
            print(JSON.stringify(C.fillPlan(kinds, kinds.map(() => true), v, metas)));
            """);
        Assert.Equal("""[[0,"BR"],[1,"SP"],[2,"52998224725"],[3,"01310100"],[4,"529.982.247-25"]]""", output);
    }

    // Os campos que o utilizador já escreveu não se mudam (só o campo onde escolheu e os vazios).
    [Fact]
    public void Script_KeepsWhatTheUserTyped()
    {
        var output = CofreClassifierTests.Run("""
            const v = { 'given-name': 'Ana', 'city': 'Lisboa', 'postal-code': '1000-001' };
            const metas = [{ filled: true }, { filled: true }, {}];
            print(JSON.stringify(C.fillPlan(['given-name', 'city', 'postal-code'], [true, true, true], v, metas, { keep: true, anchor: 0 })));
            """);
        Assert.Equal("""[[0,"Ana"],[2,"1000-001"]]""", output);
    }
}
