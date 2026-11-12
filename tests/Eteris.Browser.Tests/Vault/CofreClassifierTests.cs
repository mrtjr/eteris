using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C2 (docs/palavras-passe.md): o classificador de formulários do script do cofre, em funções puras sobre descritores
// ({tag, type, name, id, autocomplete, label, placeholder, ariaLabel, visible}), corrido no Node como o
// DevicesScriptTests. Os descritores seguem os atributos públicos dos formulários destes sites (não foram visitados) e
// formulários típicos de bancos, lojas e serviços públicos de Portugal e do Brasil. Sem Node instalado, passa.
public class CofreClassifierTests
{
    // Um caso: o nome, os campos (por ordem no DOM) e o esperado — o tipo do formulário e o tipo de cada campo («-» =
    // nenhum: o cofre não lhe toca).
    private sealed record Case(string Name, string FormKind, string[] Kinds, string Fields);

    private static Case C(string name, string formKind, string kinds, string fields) => new(name, formKind, kinds.Split(' '), fields);

    private static readonly Case[] Forms =
    [
        // ---- Inícios de sessão ----
        C("GitHub (entrar)", "login", "username password -",
            """
            [{"tag":"input","type":"text","name":"login","id":"login_field","autocomplete":"username","label":"Username or email address"},
                {"tag":"input","type":"password","name":"password","id":"password","autocomplete":"current-password","label":"Password"},
                {"tag":"input","type":"submit","name":"commit"}]
            """),
        C("Google (identificador)", "other", "username",
            """
            [{"tag":"input","type":"email","name":"identifier","id":"identifierId","autocomplete":"username","ariaLabel":"Email or phone"}]
            """),
        C("Google (palavra-passe; utilizador escondido)", "login", "- password",
            """
            [{"tag":"input","type":"email","name":"identifier","autocomplete":"username","visible":false},
                {"tag":"input","type":"password","name":"Passwd","autocomplete":"current-password","ariaLabel":"Enter your password"}]
            """),
        C("Microsoft (loginfmt e passwd)", "login", "username password",
            """
            [{"tag":"input","type":"email","name":"loginfmt","id":"i0116","placeholder":"Email, phone, or Skype"},
                {"tag":"input","type":"password","name":"passwd","id":"i0118","placeholder":"Password"}]
            """),
        C("Mercado Livre", "login", "username password",
            """
            [{"tag":"input","type":"text","name":"user_id","id":"user_id","autocomplete":"username","label":"E-mail, telefone ou usuário"},
                {"tag":"input","type":"password","name":"password","id":"password","autocomplete":"current-password","label":"Senha"}]
            """),
        C("gov.br (CPF e senha)", "login", "username password",
            """
            [{"tag":"input","type":"text","name":"accountId","id":"accountId","placeholder":"Digite seu CPF","label":"CPF"},
                {"tag":"input","type":"password","name":"password","id":"password","label":"Senha"}]
            """),
        C("Portal das Finanças (NIF e senha)", "login", "username password",
            """
            [{"tag":"input","type":"text","name":"username","placeholder":"Nº de Contribuinte","label":"Nº de Contribuinte"},
                {"tag":"input","type":"password","name":"password","placeholder":"Senha","label":"Senha"}]
            """),
        C("Banco PT (código de utilizador e código de acesso, autocomplete=off)", "login", "username password -",
            """
            [{"tag":"input","type":"text","name":"userCode","id":"cod_utilizador","autocomplete":"off","label":"Código de utilizador"},
                {"tag":"input","type":"password","name":"accessCode","id":"cod_acesso","autocomplete":"off","label":"Código de acesso"},
                {"tag":"input","type":"checkbox","name":"remember","label":"Lembrar"}]
            """),
        C("Banco BR (agência, conta e senha)", "login", "- username password",
            """
            [{"tag":"input","type":"tel","name":"agencia","label":"Agência"},
                {"tag":"input","type":"tel","name":"conta","label":"Conta"},
                {"tag":"input","type":"password","name":"senha","label":"Senha"}]
            """),
        C("Amazon (e-mail e palavra-passe)", "login", "username password",
            """
            [{"tag":"input","type":"email","name":"email","id":"ap_email","autocomplete":"username","label":"Email or mobile phone number"},
                {"tag":"input","type":"password","name":"password","id":"ap_password","autocomplete":"current-password","label":"Password"}]
            """),
        C("Facebook (email e pass)", "login", "username password",
            """
            [{"tag":"input","type":"text","name":"email","id":"email","autocomplete":"username","placeholder":"Email address or phone number"},
                {"tag":"input","type":"password","name":"pass","id":"pass","autocomplete":"current-password","placeholder":"Password"}]
            """),
        C("Reddit (sem autocomplete)", "login", "username password",
            """
            [{"tag":"input","type":"text","name":"username","id":"login-username","label":"Username"},
                {"tag":"input","type":"password","name":"password","id":"login-password","label":"Password"}]
            """),
        C("Login com telefone (type=tel)", "login", "username password",
            """
            [{"tag":"input","type":"tel","name":"phone","label":"Telemóvel"},
                {"tag":"input","type":"password","name":"pw","label":"Palavra-passe"}]
            """),
        C("Login com campos escondidos de rastreio (visíveis só os do utilizador)", "login", "- username password -",
            """
            [{"tag":"input","type":"email","name":"tracker_email","visible":false},
                {"tag":"input","type":"email","name":"email","label":"E-mail"},
                {"tag":"input","type":"password","name":"password","label":"Palavra-passe"},
                {"tag":"input","type":"password","name":"tracker_pw","visible":false}]
            """),
        C("Login sem formulário (campos soltos, ES)", "login", "username password",
            """
            [{"tag":"input","type":"text","name":"usuario","placeholder":"Usuario"},
                {"tag":"input","type":"password","name":"clave","placeholder":"Contraseña"}]
            """),
        C("Pesquisa ao lado do login", "login", "- username password",
            """
            [{"tag":"input","type":"search","name":"q","placeholder":"Pesquisar"},
                {"tag":"input","type":"text","name":"login","label":"Utilizador"},
                {"tag":"input","type":"password","name":"pass","label":"Palavra-passe"}]
            """),

        // ---- Criação de conta ----
        C("GitHub (criar conta)", "signup", "username new-password -",
            """
            [{"tag":"input","type":"email","name":"user[email]","id":"email","autocomplete":"email","label":"Email"},
                {"tag":"input","type":"password","name":"user[password]","id":"password","autocomplete":"new-password","label":"Password"},
                {"tag":"input","type":"text","name":"user[login]","id":"login","autocomplete":"off","label":"Username"}]
            """),
        C("Criar conta com confirmação (sem autocomplete)", "signup", "given-name family-name username new-password new-password",
            """
            [{"tag":"input","type":"text","name":"nome","label":"Primeiro nome"},
                {"tag":"input","type":"text","name":"apelido","label":"Apelido"},
                {"tag":"input","type":"email","name":"email","label":"E-mail"},
                {"tag":"input","type":"password","name":"password","label":"Palavra-passe"},
                {"tag":"input","type":"password","name":"password2","label":"Confirmar palavra-passe"}]
            """),
        C("Cadastro BR (CPF, e-mail, senha e confirmação)", "signup", "name tax-id username new-password new-password",
            """
            [{"tag":"input","type":"text","name":"nome","label":"Nome completo"},
                {"tag":"input","type":"tel","name":"cpf","label":"CPF"},
                {"tag":"input","type":"email","name":"email","label":"E-mail"},
                {"tag":"input","type":"password","name":"senha","autocomplete":"new-password","label":"Senha"},
                {"tag":"input","type":"password","name":"confirmacaoSenha","autocomplete":"new-password","label":"Confirme a senha"}]
            """),

        // ---- Mudança de palavra-passe ----
        C("Mudar palavra-passe (atual, nova, confirmar)", "change", "password new-password new-password",
            """
            [{"tag":"input","type":"password","name":"current","label":"Palavra-passe atual"},
                {"tag":"input","type":"password","name":"new","label":"Nova palavra-passe"},
                {"tag":"input","type":"password","name":"confirm","label":"Confirmar nova palavra-passe"}]
            """),
        C("Google (mudar a palavra-passe: nova e confirmar)", "change", "new-password new-password",
            """
            [{"tag":"input","type":"password","name":"password","autocomplete":"new-password","label":"New password"},
                {"tag":"input","type":"password","name":"confirmation_password","autocomplete":"new-password","label":"Confirm new password"}]
            """),
        C("Mudar palavra-passe com autocomplete (current + new)", "change", "password new-password",
            """
            [{"tag":"input","type":"password","name":"old","autocomplete":"current-password","label":"Senha atual"},
                {"tag":"input","type":"password","name":"nova","autocomplete":"new-password","label":"Nova senha"}]
            """),

        // ---- Códigos de uma utilização (nunca se guardam) ----
        C("Código de verificação (one-time-code)", "other", "one-time-code",
            """
            [{"tag":"input","type":"text","name":"otp","autocomplete":"one-time-code","inputMode":"numeric","label":"Código de verificação"}]
            """),
        C("2FA sem autocomplete", "other", "one-time-code",
            """
            [{"tag":"input","type":"text","name":"app_totp","id":"app_totp","label":"Authentication code"}]
            """),
        C("Código por SMS em campo de palavra-passe", "other", "one-time-code",
            """
            [{"tag":"input","type":"password","name":"sms_code","label":"Código SMS"}]
            """),

        // ---- Moradas ----
        C("Morada de Portugal", "address", "name street-address address-complement postal-code city region tel",
            """
            [{"tag":"input","type":"text","name":"nome","label":"Nome"},
                {"tag":"input","type":"text","name":"morada","label":"Morada"},
                {"tag":"input","type":"text","name":"morada2","label":"Andar / fração"},
                {"tag":"input","type":"text","name":"cp","label":"Código postal","placeholder":"0000-000"},
                {"tag":"input","type":"text","name":"localidade","label":"Localidade"},
                {"tag":"select","type":"select-one","name":"distrito","label":"Distrito"},
                {"tag":"input","type":"tel","name":"telemovel","label":"Telemóvel"}]
            """),
        C("Endereço do Brasil (CEP primeiro)", "address", "postal-code street-address address-number address-complement neighborhood city region",
            """
            [{"tag":"input","type":"tel","name":"cep","label":"CEP"},
                {"tag":"input","type":"text","name":"logradouro","label":"Endereço"},
                {"tag":"input","type":"text","name":"numero","label":"Número"},
                {"tag":"input","type":"text","name":"complemento","label":"Complemento"},
                {"tag":"input","type":"text","name":"bairro","label":"Bairro"},
                {"tag":"input","type":"text","name":"cidade","label":"Cidade"},
                {"tag":"select","type":"select-one","name":"uf","label":"Estado"}]
            """),
        C("Morada com autocomplete (shipping)", "address", "given-name family-name street-address address-line2 city region postal-code country email",
            """
            [{"tag":"input","type":"text","name":"fn","autocomplete":"shipping given-name"},
                {"tag":"input","type":"text","name":"ln","autocomplete":"shipping family-name"},
                {"tag":"input","type":"text","name":"a1","autocomplete":"shipping address-line1"},
                {"tag":"input","type":"text","name":"a2","autocomplete":"shipping address-line2"},
                {"tag":"input","type":"text","name":"c","autocomplete":"shipping address-level2"},
                {"tag":"input","type":"text","name":"s","autocomplete":"shipping address-level1"},
                {"tag":"input","type":"text","name":"z","autocomplete":"shipping postal-code"},
                {"tag":"select","type":"select-one","name":"co","autocomplete":"shipping country"},
                {"tag":"input","type":"email","name":"e","autocomplete":"email"}]
            """),
        C("Checkout BR com CPF", "address", "name tax-id postal-code street-address address-number neighborhood city",
            """
            [{"tag":"input","type":"text","name":"nomeCompleto","label":"Nome completo"},
                {"tag":"input","type":"tel","name":"documento","label":"CPF"},
                {"tag":"input","type":"tel","name":"zip","label":"CEP"},
                {"tag":"input","type":"text","name":"rua","label":"Rua"},
                {"tag":"input","type":"text","name":"num","label":"Nº"},
                {"tag":"input","type":"text","name":"bairro","label":"Bairro"},
                {"tag":"input","type":"text","name":"cidade","label":"Cidade"}]
            """),
        C("Faturação em Portugal com NIF", "address", "name tax-id street-address postal-code city",
            """
            [{"tag":"input","type":"text","name":"billing_name","label":"Nome"},
                {"tag":"input","type":"text","name":"vat","label":"NIF (contribuinte)"},
                {"tag":"input","type":"text","name":"billing_address","label":"Morada"},
                {"tag":"input","type":"text","name":"billing_postcode","label":"Código postal"},
                {"tag":"input","type":"text","name":"billing_city","label":"Cidade"}]
            """),
        C("Estado civil e data de nascimento não são morada", "other", "name - -",
            """
            [{"tag":"input","type":"text","name":"nome","label":"Nome"},
                {"tag":"select","type":"select-one","name":"estadoCivil","label":"Estado civil"},
                {"tag":"input","type":"text","name":"nascimento","label":"Data de nascimento"}]
            """),

        // ---- Cartões ----
        C("Cartão com autocomplete", "payment", "cc-name cc-number cc-exp cc-csc",
            """
            [{"tag":"input","type":"text","name":"ccname","autocomplete":"cc-name","label":"Nome no cartão"},
                {"tag":"input","type":"text","name":"cardnumber","autocomplete":"cc-number","inputMode":"numeric","label":"Número do cartão"},
                {"tag":"input","type":"text","name":"exp-date","autocomplete":"cc-exp","placeholder":"MM / AA"},
                {"tag":"input","type":"text","name":"cvc","autocomplete":"cc-csc","label":"CVC"}]
            """),
        C("Cartão sem autocomplete (EN)", "payment", "cc-number cc-exp cc-csc cc-name",
            """
            [{"tag":"input","type":"tel","name":"cardNumber","placeholder":"Card number"},
                {"tag":"input","type":"tel","name":"expiry","placeholder":"MM/YY"},
                {"tag":"input","type":"password","name":"cvv","placeholder":"CVV"},
                {"tag":"input","type":"text","name":"holder","placeholder":"Cardholder name"}]
            """),
        C("Cartão com mês e ano em listas (BR)", "payment", "cc-number cc-name cc-exp-month cc-exp-year cc-csc",
            """
            [{"tag":"input","type":"tel","name":"numeroCartao","label":"Número do cartão"},
                {"tag":"input","type":"text","name":"nomeTitular","label":"Nome do titular (como no cartão)"},
                {"tag":"select","type":"select-one","name":"mesValidade","label":"Mês de validade"},
                {"tag":"select","type":"select-one","name":"anoValidade","label":"Ano de validade"},
                {"tag":"input","type":"tel","name":"codigoSeguranca","label":"Código de segurança"}]
            """),
        C("Validade sem número de cartão não é cartão", "other", "- -",
            """
            [{"tag":"input","type":"text","name":"validade","label":"Validade"},
                {"tag":"input","type":"text","name":"titular","label":"Titular"}]
            """),

        // ---- Outros dados ----
        C("IBAN", "other", "name iban",
            """
            [{"tag":"input","type":"text","name":"beneficiario","label":"Nome"},
                {"tag":"input","type":"text","name":"iban","label":"IBAN"}]
            """),
        C("Veículo (matrícula/placa)", "other", "plate",
            """
            [{"tag":"input","type":"text","name":"matricula","label":"Matrícula"}]
            """),
        C("Documento (passaporte)", "other", "id-number",
            """
            [{"tag":"input","type":"text","name":"passport","label":"Número do passaporte"}]
            """),
        C("Pesquisa e newsletter", "other", "- email",
            """
            [{"tag":"input","type":"search","name":"q","label":"Pesquisar"},
                {"tag":"input","type":"email","name":"newsletter","label":"O seu e-mail"}]
            """),
    ];

    [Fact]
    public void ThereAreThirtyOrMoreRealWorldForms() => Assert.True(Forms.Length >= 30, $"{Forms.Length} formulários");

    [Fact]
    public void Forms_AreClassified()
    {
        var input = JsonSerializer.Serialize(Forms.Select(f => new { name = f.Name, fields = JsonDocument.Parse(f.Fields).RootElement }));
        var output = Run($$"""
            const forms = {{input}};
            const out = forms.map(f => {
              const fields = f.fields.map(d => Object.assign({}, d, { kind: C.classifyField(d) }));
              const r = C.classifyForm(fields);
              return { name: f.name, formKind: r.formKind, kinds: r.kinds.map(k => k || '-') };
            });
            print(JSON.stringify(out));
            """);
        if (output is null) return;
        var results = JsonDocument.Parse(output).RootElement.EnumerateArray().ToList();
        var failures = new List<string>();
        for (var i = 0; i < Forms.Length; i++)
        {
            var got = results[i];
            var kinds = got.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()).ToArray();
            var formKind = got.GetProperty("formKind").GetString();
            if (formKind != Forms[i].FormKind || !kinds.SequenceEqual(Forms[i].Kinds))
                failures.Add($"{Forms[i].Name}: esperado {Forms[i].FormKind} [{string.Join(' ', Forms[i].Kinds)}], veio {formKind} [{string.Join(' ', kinds)}]");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // O autocomplete manda primeiro; «off» é ignorado (nas palavras-passe, como o Chrome e o Firefox); as secções e os
    // modificadores (shipping, billing, home…) não contam.
    [Fact]
    public void Autocomplete_ComesFirst()
    {
        var output = Run("""
            const k = d => C.classifyField(Object.assign({ tag: 'input', type: 'text' }, d)) || '-';
            print([
              k({ autocomplete: 'section-a billing postal-code', name: 'x' }),
              k({ autocomplete: 'off', name: 'cep' }),
              k({ type: 'password', autocomplete: 'off', name: 'pw' }),
              k({ autocomplete: 'current-password', type: 'text', name: 'pw' }),
              k({ autocomplete: 'home tel', name: 'x' }),
              k({ autocomplete: 'nickname', name: 'email' }),
              k({ autocomplete: 'address-level3', name: 'x' }),
              k({ type: 'hidden', name: 'username', autocomplete: 'username' }),
              k({ type: 'checkbox', name: 'remember' }),
              k({ tag: 'textarea', type: 'textarea', name: 'morada' }),
              k({ tag: 'textarea', type: 'textarea', name: 'mensagem' }),
              k({ tag: 'select', type: 'select-one', name: 'email' }),
            ].join(' '));
            """);
        if (output is null) return;
        Assert.Equal("postal-code postal-code password password tel email neighborhood - - street-address - -", output);
    }

    // Só se oferece num campo que se vê: tamanho, display, visibility, opacidade (com a dos antepassados), dentro do ecrã
    // e não tapado (o ponto do centro devolve o próprio campo ou um descendente).
    [Fact]
    public void Visibility()
    {
        var output = Run("""
            const base = { width: 200, height: 30, display: 'block', visibility: 'visible', opacity: 1, inViewport: true, hit: true };
            const v = d => C.isVisible(Object.assign({}, base, d)) ? 1 : 0;
            print([v({}), v({ width: 0 }), v({ height: 0 }), v({ display: 'none' }), v({ visibility: 'hidden' }),
              v({ visibility: 'collapse' }), v({ opacity: 0 }), v({ opacity: 0.05 }), v({ opacity: 0.5 }), v({ inViewport: false }),
              v({ hit: false }), v({ width: 1, height: 1 })].join(''));
            """);
        if (output is null) return;
        Assert.Equal("100000000000", output);
    }

    // Revisão: os campos que o utilizador não vê não contam (formulários escondidos). Opacidade efetiva quase 1, um tamanho
    // realista (10×10), texto com cor e o iframe da mesma origem visível e opaco.
    [Fact]
    public void Visibility_HiddenFieldTricks()
    {
        var output = Run("""
            const base = { width: 200, height: 30, display: 'block', visibility: 'visible', opacity: 1, inViewport: true, hit: true };
            const v = d => C.isVisible(Object.assign({}, base, d)) ? 1 : 0;
            print([v({ opacity: 0.95 }), v({ opacity: 0.85 }), v({ width: 9 }), v({ height: 9 }), v({ width: 10, height: 10 }),
              v({ textVisible: false }), v({ textVisible: true }), v({ frameVisible: false }), v({ frameVisible: true })].join(''));
            """);
        if (output is null) return;
        Assert.Equal("100010101", output);
    }

    // Tapado: o campo conta só se a maior parte dos pontos de amostra (centro e quatro pontos para dentro dos cantos) cair
    // no próprio campo (ou num rótulo transparente por cima, como os rótulos flutuantes).
    [Fact]
    public void Covered_ByMostSamplePoints()
    {
        var output = Run("""
            print([C.uncovered([true, true, true, true, true]), C.uncovered([true, true, true, false, false]),
              C.uncovered([true, true, false, false, false]), C.uncovered([false, false, false, false, false]), C.uncovered([])].join(','));
            """);
        if (output is null) return;
        Assert.Equal("true,true,false,false,false", output);
    }

    // A lista só abre sozinha com um gesto do utilizador: um botão do rato que desceu em cima do campo (ou do rótulo dele)
    // ou a tecla Tab, há menos de um segundo. Um campo.focus() da página, um clique noutro sítio ou um gesto antigo não
    // contam.
    [Fact]
    public void Gesture_OnlyATrustedPointerOnTheFieldOrTab_JustBefore()
    {
        var output = Run("""
            const g = (last, now, onField) => C.gesture(last, now, onField) ? 1 : 0;
            print([g(null, 100, true), g({ at: 50, pointer: true }, 100, true), g({ at: 50, pointer: true }, 100, false),
              g({ at: 50, key: 'Tab' }, 100, false), g({ at: 50, key: 'Enter' }, 100, false), g({ at: 50, pointer: true }, 1200, true),
              g({ at: 200, pointer: true }, 100, true)].join(''));
            """);
        if (output is null) return;
        Assert.Equal("0101000", output);
    }

    // O que a página consegue fingir com eventos sintéticos não conta: o envio, o rolar, o escrever, o menu de contexto
    // e o Tab só valem com isTrusted. Cada tratador tem de começar por sair quando o evento não é confiável — não basta
    // a palavra isTrusted algures no corpo (com a verificação invertida, «if (e.isTrusted) return;», passava).
    [Theory]
    [InlineData("onSubmit")]
    [InlineData("onInput")]
    [InlineData("onScroll")]
    [InlineData("onEnter")]
    [InlineData("onButton")]
    [InlineData("onPointerDown")]
    [InlineData("onContext")]
    [InlineData("onKeyGesture")]
    public void Handlers_IgnoreSyntheticEvents(string handler)
    {
        var source = VaultScripts.Source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = source.IndexOf("const " + handler + " = ", StringComparison.Ordinal);
        if (start < 0) start = source.IndexOf("function " + handler + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, handler);
        var lineEnd = source.IndexOf('\n', start);
        var nextEnd = source.IndexOf('\n', lineEnd + 1);
        var first = source[(lineEnd + 1)..nextEnd].Trim();
        Assert.True(ReturnsFirstOnSynthetic(first), handler + ": " + first);
    }

    // A guarda tem de recusar a verificação invertida e a enfraquecida por um «e».
    [Theory]
    [InlineData("if (!e.isTrusted) return;", true)]
    [InlineData("if (filling || !e.isTrusted) return;", true)]
    [InlineData("if (e.key !== 'Enter' || !e.isTrusted) return;", true)]
    [InlineData("if (!e || !e.isTrusted || !focused || scrollQueued) return;", true)]
    [InlineData("if (e.isTrusted) return;", false)]
    [InlineData("if (!e.isTrusted && filling) return;", false)]
    [InlineData("const field = deepTarget(e);", false)]
    public void Guard_RejectsInvertedOrWeakenedChecks(string statement, bool expected)
        => Assert.Equal(expected, ReturnsFirstOnSynthetic(statement));

    // Verdadeiro só se a instrução for «if (…) return;», sem «&&», com «!e.isTrusted» como um dos operandos do «ou».
    // Limite: é uma guarda de texto; a execução dos ouvintes no motor real fica para a 2.ª fatia dos testes ao vivo
    // (TCO-01).
    internal static bool ReturnsFirstOnSynthetic(string firstStatement)
    {
        var match = System.Text.RegularExpressions.Regex.Match(firstStatement, @"^if \((.+)\) return;$");
        if (!match.Success) return false;
        var condition = match.Groups[1].Value;
        if (condition.Contains("&&", StringComparison.Ordinal)) return false;
        return condition.Split(" || ").Any(operand => operand == "!e.isTrusted");
    }

    // O MutationObserver só reage a nós com campos e espera 250 ms sem mudanças.
    [Fact]
    public void Mutations_AreDebounced_AndOnlyFieldsCount()
    {
        var output = Run("""
            let now = 0; const queue = [];
            const timers = { set(fn, ms) { const t = { at: now + ms, fn }; queue.push(t); return t; }, clear(t) { const i = queue.indexOf(t); if (i >= 0) queue.splice(i, 1); } };
            const advance = ms => { now += ms; for (const t of queue.slice()) if (t.at <= now) { queue.splice(queue.indexOf(t), 1); t.fn(); } };
            let runs = 0;
            const d = C.debounce(() => runs++, 250, timers);
            d(); advance(100); d(); advance(100); d(); advance(249);
            const before = runs; advance(1);
            const node = (name, inner) => ({ nodeType: 1, nodeName: name, querySelector: () => inner ? {} : null });
            const text = { nodeType: 3, nodeName: '#text' };
            const r = recs => C.touchesFields(recs) ? 1 : 0;
            print([before, runs, r([{ addedNodes: [text], removedNodes: [] }]), r([{ addedNodes: [node('DIV', false)], removedNodes: [] }]),
              r([{ addedNodes: [node('INPUT', false)], removedNodes: [] }]), r([{ addedNodes: [], removedNodes: [node('FORM', true)] }]),
              r([{ addedNodes: [node('SECTION', true)], removedNodes: [] }])].join(','));
            """);
        if (output is null) return;
        Assert.Equal("0,1,0,0,1,1,1", output);
    }

    // Os tipos que o script pode mandar são os que o esquema do navegador aceita (VaultMessages.FieldKinds).
    [Fact]
    public void ScriptKinds_AreInTheMessageSchema()
    {
        var output = Run("print(JSON.stringify(C.KINDS));");
        if (output is null) return;
        var kinds = JsonSerializer.Deserialize<string[]>(output)!;
        Assert.NotEmpty(kinds);
        Assert.All(kinds, k => Assert.Contains(k, VaultMessages.FieldKinds));
    }

    // O texto do rótulo: acentos, camelCase, separadores e «nº».
    [Fact]
    public void Text_IsNormalized()
    {
        var output = Run("""print([C.norm('Nº de Contribuinte'), C.norm('cardNumber'), C.norm('user[email]'), C.norm('Código  Postal'), C.norm('n.º porta')].join('|'));""");
        if (output is null) return;
        Assert.Equal("numero de contribuinte|card number|user email|codigo postal|numero porta", output);
    }

    public static string? Run(string body)
    {
        var classifier = VaultScripts.Source.Replace(VaultScripts.BindingPlaceholder, "null", StringComparison.Ordinal)
            .Replace(VaultScripts.OfferPlaceholder, "false", StringComparison.Ordinal);
        var script = "const C = " + classifier.TrimEnd().TrimEnd(';') + ";\n"
                     + "const print = s => process.stdout.write(String(s));\n"
                     + "(async () => {\n" + body + "\n})().catch(e => { console.error(e); process.exit(1); });";
        var file = Path.Combine(Path.GetTempPath(), $"eteris-cofre-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            Process? node;
            try
            {
                node = Process.Start(new ProcessStartInfo("node", [file])
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                });
            }
            catch (System.ComponentModel.Win32Exception) { return null; } // Node não instalado: não há como verificar aqui.
            if (node is null) return null;
            var output = node.StandardOutput.ReadToEnd();
            var error = node.StandardError.ReadToEnd();
            node.WaitForExit(30_000);
            Assert.True(node.ExitCode == 0, error);
            return output.Replace("\r\n", "\n");
        }
        finally { File.Delete(file); }
    }
}
