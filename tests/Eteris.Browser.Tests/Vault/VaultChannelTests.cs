using System.Text.Json;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C1 (docs/palavras-passe.md): o canal entre o script do cofre (mundo isolado) e o navegador. As mensagens chegam pela
// ligação do mundo isolado e são validadas por um esquema estrito (o que não encaixa é descartado); a origem de cada
// contexto é a que o processo do navegador conhece, nunca a que a página diz.
public class VaultChannelTests
{
    // ---- Mensagens ----

    [Fact]
    public void Ready_IsAccepted()
    {
        var message = VaultMessages.Parse("""{"t":"ready","v":1}""");
        Assert.NotNull(message);
        Assert.Equal(VaultMessageType.Ready, message.Type);
        Assert.Equal(1, message.Version);
    }

    [Fact]
    public void Focus_CarriesFieldFormKindRectAndPrefix()
    {
        var message = VaultMessages.Parse("""{"t":"focus","field":"f3","form":"m1","kind":"username","formKind":"login","rect":[10.5,20,300,32],"visible":true,"prefix":"ana"}""");
        Assert.NotNull(message);
        Assert.Equal(VaultMessageType.Focus, message.Type);
        Assert.Equal("f3", message.Field);
        Assert.Equal("m1", message.Form);
        Assert.Equal("username", message.Kind);
        Assert.Equal("login", message.FormKind);
        Assert.Equal(new VaultRect(10.5, 20, 300, 32), message.Rect);
        Assert.True(message.Visible);
        Assert.Equal("ana", message.Prefix);
    }

    [Fact]
    public void Submit_CarriesTheSecrets_ButToStringNeverShowsThem()
    {
        var message = VaultMessages.Parse("""{"t":"submit","form":"m1","formKind":"login","username":"ana.teste","password":"Inventada-1!"}""");
        Assert.NotNull(message);
        Assert.Equal("Inventada-1!", message.Password);
        Assert.DoesNotContain("Inventada", message.ToString());
        Assert.DoesNotContain("ana.teste", message.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"ready\"")]
    [InlineData("{")]
    [InlineData("""{"v":1}""")]
    [InlineData("""{"t":"desconhecido"}""")]
    [InlineData("""{"t":"READY"}""")]
    [InlineData("""{"t":1}""")]
    // Propriedades a mais: o esquema é estrito.
    [InlineData("""{"t":"ready","v":1,"extra":true}""")]
    [InlineData("""{"t":"blur","field":"f1","password":"x"}""")]
    // Tipos errados.
    [InlineData("""{"t":"focus","field":"f1","form":"m1","kind":"username","rect":"10,20,30,40","visible":true}""")]
    [InlineData("""{"t":"focus","field":"f1","form":"m1","kind":"username","rect":[1,2,3],"visible":true}""")]
    [InlineData("""{"t":"focus","field":"f1","form":"m1","kind":"username","rect":[1,2,-3,4],"visible":true}""")]
    [InlineData("""{"t":"focus","field":"f1","form":"m1","kind":"username","rect":[1e9,2,3,4],"visible":true}""")]
    [InlineData("""{"t":"focus","field":"f1","form":"m1","kind":"username","rect":[1,2,3,4],"visible":"sim"}""")]
    [InlineData("""{"t":"focus","field":12,"form":"m1","kind":"username","rect":[1,2,3,4],"visible":true}""")]
    // IDs efémeros: só letras minúsculas e algarismos, curtos.
    [InlineData("""{"t":"focus","field":"F1","form":"m1","kind":"username","rect":[1,2,3,4],"visible":true}""")]
    [InlineData("""{"t":"focus","field":"f1<script>","form":"m1","kind":"username","rect":[1,2,3,4],"visible":true}""")]
    [InlineData("""{"t":"focus","field":"f12345678901234567","form":"m1","kind":"username","rect":[1,2,3,4],"visible":true}""")]
    // Tipos de campo e de formulário fora da lista.
    [InlineData("""{"t":"focus","field":"f1","form":"m1","kind":"segredo","rect":[1,2,3,4],"visible":true}""")]
    [InlineData("""{"t":"focus","field":"f1","form":"m1","kind":"username","formKind":"banco","rect":[1,2,3,4],"visible":true}""")]
    // Campos obrigatórios em falta.
    [InlineData("""{"t":"focus","form":"m1","kind":"username","rect":[1,2,3,4],"visible":true}""")]
    [InlineData("""{"t":"submit","username":"a","password":"b"}""")]
    public void Invalid_IsDropped(string payload) => Assert.Null(VaultMessages.Parse(payload));

    [Fact]
    public void Oversized_IsDropped()
    {
        var prefix = new string('a', 200);
        var ok = $$"""{"t":"input","field":"f1","prefix":"{{prefix}}"}""";
        Assert.NotNull(VaultMessages.Parse(ok));
        // Mais de 16 KiB (em UTF-8) não se lê sequer.
        var big = $$"""{"t":"submit","form":"m1","formKind":"login","username":"a","password":"{{new string('é', 9000)}}"}""";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(big) > VaultMessages.MaxBytes);
        Assert.Null(VaultMessages.Parse(big));
    }

    [Fact]
    public void LongTexts_AreDropped()
    {
        Assert.Null(VaultMessages.Parse($$"""{"t":"input","field":"f1","prefix":"{{new string('a', VaultMessages.MaxPrefix + 1)}}"}"""));
        Assert.Null(VaultMessages.Parse($$"""{"t":"submit","form":"m1","formKind":"login","username":"a","password":"{{new string('a', VaultMessages.MaxSecret + 1)}}"}"""));
    }

    [Fact]
    public void Generate_CarriesTheFieldRules()
    {
        var message = VaultMessages.Parse("""{"t":"generate","field":"f2","form":"m1","minLength":8,"maxLength":32,"pattern":"[A-Za-z0-9]+"}""");
        Assert.NotNull(message);
        Assert.Equal(8, message.MinLength);
        Assert.Equal(32, message.MaxLength);
        Assert.Equal("[A-Za-z0-9]+", message.Pattern);
        Assert.Null(VaultMessages.Parse("""{"t":"generate","field":"f2","form":"m1","minLength":-1}"""));
        Assert.Null(VaultMessages.Parse("""{"t":"generate","field":"f2","form":"m1","minLength":1.5}"""));
    }

    [Fact]
    public void EveryKindInTheSchema_IsAccepted()
    {
        foreach (var kind in VaultMessages.FieldKinds)
            Assert.NotNull(VaultMessages.Parse($$"""{"t":"focus","field":"f1","form":"m1","kind":"{{kind}}","rect":[1,2,3,4],"visible":false}"""));
        Assert.Contains("username", VaultMessages.FieldKinds);
        Assert.Contains("password", VaultMessages.FieldKinds);
        Assert.Contains("new-password", VaultMessages.FieldKinds);
        Assert.Contains("cc-csc", VaultMessages.FieldKinds);
        Assert.Contains("tax-id", VaultMessages.FieldKinds);
    }

    // ---- Contextos: contextId → (frame, documento, origem do navegador) ----

    [Fact]
    public void Contexts_MapToTheBrowserOrigin()
    {
        var contexts = new VaultContexts();
        Assert.True(contexts.TryClaim("F1", "L1"));
        Assert.True(contexts.Add(new VaultContext(7, "F1", "L1", "https://exemplo.test", IsMain: true)));
        Assert.Equal("https://exemplo.test", contexts.Get(7)?.Origin);
        Assert.Null(contexts.Get(8));
    }

    [Fact]
    public void Contexts_SameDocumentIsSetUpOnce()
    {
        var contexts = new VaultContexts();
        Assert.True(contexts.TryClaim("F1", "L1"));
        Assert.False(contexts.TryClaim("F1", "L1"));
        // Outro documento no mesmo frame.
        Assert.True(contexts.TryClaim("F1", "L2"));
    }

    [Fact]
    public void Contexts_AFailedSetUpCanBeRetried()
    {
        var contexts = new VaultContexts();
        Assert.True(contexts.TryClaim("F1", "L1"));
        contexts.Release("F1", "L1");
        Assert.True(contexts.TryClaim("F1", "L1"));
    }

    [Fact]
    public void Contexts_MainNavigationForgetsEverything_ChildOnlyItsFrame()
    {
        var contexts = new VaultContexts();
        contexts.TryClaim("TOP", "L1");
        Assert.True(contexts.Add(new VaultContext(1, "TOP", "L1", "https://exemplo.test", IsMain: true)));
        contexts.TryClaim("IF", "L2");
        Assert.True(contexts.Add(new VaultContext(2, "IF", "L2", "https://exemplo.test", IsMain: false)));
        contexts.FrameNavigated("IF", "L3", isMain: false);
        Assert.NotNull(contexts.Get(1));
        Assert.Null(contexts.Get(2));
        Assert.True(contexts.TryClaim("IF", "L3"));
        contexts.FrameNavigated("TOP", "L9", isMain: true);
        Assert.Null(contexts.Get(1));
        Assert.Equal(0, contexts.Count);
        Assert.True(contexts.TryClaim("TOP", "L9"));
    }

    // O evento do documento novo chega depois de o navegador já o ter preparado (FrameLoadStart): não se prepara outra vez.
    [Fact]
    public void Contexts_NavigationEventOfTheDocumentAlreadySetUp_KeepsIt()
    {
        var contexts = new VaultContexts();
        Assert.True(contexts.TryClaim("TOP", "L2"));
        Assert.True(contexts.Add(new VaultContext(5, "TOP", "L2", "https://exemplo.test", IsMain: true)));
        contexts.FrameNavigated("TOP", "L2", isMain: true);
        Assert.NotNull(contexts.Get(5));
        Assert.False(contexts.TryClaim("TOP", "L2"));
    }

    // Um documento que já saiu (preparação atrasada) ou um contexto já conhecido não entram.
    [Fact]
    public void Contexts_LateOrRepeatedSetUp_IsIgnored()
    {
        var contexts = new VaultContexts();
        Assert.True(contexts.TryClaim("TOP", "L1"));
        contexts.FrameNavigated("TOP", "L2", isMain: true);
        Assert.False(contexts.Add(new VaultContext(3, "TOP", "L1", "https://exemplo.test", IsMain: true)));
        Assert.Null(contexts.Get(3));
        Assert.True(contexts.TryClaim("TOP", "L2"));
        Assert.True(contexts.Add(new VaultContext(4, "TOP", "L2", "https://exemplo.test", IsMain: true)));
        Assert.False(contexts.Add(new VaultContext(4, "TOP", "L2", "https://exemplo.test", IsMain: true)));
    }

    // ---- Onde o script entra: só documentos http(s) com a origem que o navegador conhece ----

    [Theory]
    [InlineData("https://exemplo.test/entrar", "https://exemplo.test/entrar", "https://exemplo.test")]
    [InlineData("http://127.0.0.1:8896/login", "http://127.0.0.1:8896/", "http://127.0.0.1:8896")]
    // iframe da mesma origem que a página de topo (CEF).
    [InlineData("https://exemplo.test/frame.html", "https://exemplo.test/", "https://exemplo.test")]
    public void Eligible_SameOriginAsTheBrowserKnows(string frameUrl, string mainUrl, string origin)
        => Assert.Equal(origin, VaultChannel.EligibleOrigin(frameUrl, mainUrl));

    [Theory]
    // iframe de outro site (v1: nunca).
    [InlineData("https://pagamentos.outro.test/", "https://exemplo.test/")]
    // Mesmo site, outra origem.
    [InlineData("https://login.exemplo.test/", "https://exemplo.test/")]
    [InlineData("http://exemplo.test/", "https://exemplo.test/")]
    // O renderer diz uma coisa, o navegador outra (corrida ou mentira): não entra.
    [InlineData("https://exemplo.test/", "https://outro.test/")]
    [InlineData("about:blank", "https://exemplo.test/")]
    [InlineData("about:srcdoc", "https://exemplo.test/")]
    [InlineData("data:text/html,<form>", "data:text/html,<form>")]
    [InlineData("eteris://settings/", "eteris://settings/")]
    [InlineData("file:///C:/login.html", "file:///C:/login.html")]
    [InlineData("https://exemplo.test/", null)]
    [InlineData(null, "https://exemplo.test/")]
    public void NotEligible(string? frameUrl, string? mainUrl) => Assert.Null(VaultChannel.EligibleOrigin(frameUrl, mainUrl));

    // ---- Do navegador para o script: só os métodos conhecidos, argumento serializado (nunca concatenado à mão) ----

    [Fact]
    public void CallExpression_SerializesTheArgument()
    {
        const string hostile = "\"); alert(1); (\"</script>\u2028";
        var expression = VaultChannel.CallExpression("fill", new { form = "m1", values = new Dictionary<string, string> { ["password"] = hostile } });
        Assert.StartsWith("(Object.prototype.hasOwnProperty.call(globalThis, \"__cofre\") ? globalThis.__cofre.fill(", expression);
        // O texto da página nunca fecha a cadeia: as aspas e o resto vão escapados no JSON.
        Assert.DoesNotContain("\"); alert(1)", expression);
        Assert.DoesNotContain("</script>", expression);
        Assert.DoesNotContain("\u2028", expression);
        var start = expression.IndexOf("fill(", StringComparison.Ordinal) + 5;
        var json = expression[start..expression.LastIndexOf(") : null)", StringComparison.Ordinal)];
        Assert.Equal(hostile, JsonDocument.Parse(json).RootElement.GetProperty("values").GetProperty("password").GetString());
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("fill.call")]
    [InlineData("constructor")]
    public void CallExpression_RefusesUnknownMethods(string method)
        => Assert.Throws<ArgumentException>(() => VaultChannel.CallExpression(method, null));

    // ---- O script ----

    [Fact]
    public void Script_CarriesTheBindingNameAsJson()
    {
        var script = VaultScripts.Cofre("eabc123");
        Assert.Contains("\"eabc123\"", script);
        Assert.DoesNotContain(VaultScripts.BindingPlaceholder, script);
        Assert.Throws<ArgumentException>(() => VaultScripts.Cofre("a\"b"));
        Assert.Throws<ArgumentException>(() => VaultScripts.Cofre(""));
    }

    [Fact]
    public void BindingName_IsRandomPerSession()
    {
        var a = VaultChannel.NewBindingName();
        var b = VaultChannel.NewBindingName();
        Assert.NotEqual(a, b);
        Assert.Matches("^e[0-9a-f]{16}$", a);
    }

    // O mundo isolado tem um nome fixo, que nenhuma página vê.
    [Fact]
    public void WorldName_IsFixed() => Assert.Equal("eteris-cofre", VaultChannel.WorldName);
}
