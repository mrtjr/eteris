using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C3 (docs/palavras-passe.md): o preenchimento no script do cofre, corrido no Node sobre um campo de imitação. O valor
// entra pelo setter nativo do protótipo (os frameworks como o React põem um setter próprio na instância para saberem o
// último valor que viram; passar por cima dele é o que os faz reparar na mudança), depois do foco, com os eventos input e
// change a borbulhar, como o Chrome e as extensões fazem.
public class CofreFillTests
{
    private const string Setup = """
        const calls = [];
        class Field extends EventTarget {
          constructor(name) { super(); this.name = name; this._v = ''; }
          focus() { calls.push('foco:' + this.name); }
        }
        Object.defineProperty(Field.prototype, 'value', { configurable: true,
          get() { return this._v; }, set(v) { calls.push('nativo:' + this.name); this._v = v; } });
        const watch = f => {
          f.addEventListener('input', e => calls.push('input:' + e.bubbles + ':' + f._v));
          f.addEventListener('change', e => calls.push('change:' + e.bubbles));
        };
        """;

    [Fact]
    public void Fill_UsesTheNativeSetter_ThenInputAndChange()
    {
        var output = CofreClassifierTests.Run(Setup + """
            const f = new Field('pw');
            // O «React» da página: um setter próprio na instância.
            Object.defineProperty(f, 'value', { configurable: true, get() { return 'visto-pela-pagina'; }, set(v) { calls.push('pagina:' + v); } });
            watch(f);
            const ok = C.fillField(f, 'Inventada-1!');
            print(ok + ' ' + calls.join(','));
            """);
        Assert.Equal("true foco:pw,nativo:pw,input:true:Inventada-1!,change:true", output);
    }

    [Fact]
    public void Fill_RefusesWhatHasNoNativeValue()
    {
        var output = CofreClassifierTests.Run(Setup + """
            print([C.fillField({ focus() {} }, 'x'), C.fillField(null, 'x'), C.fillField(new Field('a'), 42)].join(','));
            """);
        Assert.Equal("false,false,false", output);
    }

    // Os valores por tipo: no início de sessão, o utilizador e a palavra-passe atual; nunca a nova, nem o CVC, nem campos
    // escondidos.
    [Fact]
    public void Plan_FillsOnlyTheVisibleLoginFieldsOfTheForm()
    {
        var output = CofreClassifierTests.Run("""
            const kinds = ['username', 'password', 'new-password', 'cc-csc', null, 'password'];
            const visible = [true, true, true, true, true, false];
            const plan = C.fillPlan(kinds, visible, { username: 'ana', password: 'Inventada-1!' });
            print(JSON.stringify(plan));
            """);
        Assert.Equal("""[[0,"ana"],[1,"Inventada-1!"]]""", output);
    }

    // O gerador (C5): a palavra-passe nova e a confirmação, nunca a atual.
    [Fact]
    public void Plan_Generated_FillsTheNewPasswordAndItsConfirmation()
    {
        var output = CofreClassifierTests.Run("""
            print(JSON.stringify(C.fillPlan(['username', 'password', 'new-password', 'new-password'], [true, true, true, true], { 'new-password': 'Gerada-1' })));
            """);
        Assert.Equal("""[[2,"Gerada-1"],[3,"Gerada-1"]]""", output);
    }

    // O menu de contexto (C5, C6): a mensagem diz o campo, o formulário e, numa palavra-passe, as restrições.
    [Fact]
    public void ContextMessage_CarriesTheFieldAndTheLimits()
    {
        var message = VaultMessages.Parse("""{"t":"context","field":"f1","form":"m1","kind":"new-password","rect":[1,2,3,4],"minLength":8,"maxLength":16,"pattern":"[a-z]+"}""");
        Assert.NotNull(message);
        Assert.Equal(VaultMessageType.Context, message.Type);
        Assert.Equal((8, 16, "[a-z]+"), (message.MinLength, message.MaxLength, message.Pattern));
        Assert.NotNull(VaultMessages.Parse("""{"t":"context","field":"f1","form":"m1"}"""));
        Assert.Null(VaultMessages.Parse("""{"t":"context","field":"f1"}"""));
    }

    [Fact]
    public void Plan_WithoutUsername_FillsOnlyThePassword()
    {
        var output = CofreClassifierTests.Run("""
            print(JSON.stringify(C.fillPlan(['username', 'password'], [true, true], { password: 'x' })) + ' ' +
                  JSON.stringify(C.fillPlan(['username', 'password'], [true, true], { username: '', password: 'x' })) + ' ' +
                  JSON.stringify(C.fillPlan(['username'], [true], { username: 'ana', password: 'x' })));
            """);
        Assert.Equal("""[[1,"x"]] [[1,"x"]] [[0,"ana"]]""", output);
    }
}
