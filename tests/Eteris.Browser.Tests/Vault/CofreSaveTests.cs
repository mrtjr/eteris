using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C4 (docs/palavras-passe.md): o que o script do cofre manda no envio de um formulário (corrido no Node). Só inícios de
// sessão, criações de conta e mudanças, com a palavra-passe escrita; o código de uma utilização nunca vai.
public class CofreSaveTests
{
    [Fact]
    public void Login_SendsTheUserAndTheCurrentPassword()
    {
        var output = CofreClassifierTests.Run("""
            print(JSON.stringify(C.submission({ formKind: 'login', kinds: ['username', 'password'], user: 0 }, ['ana@exemplo.test', 'Inventada-1!'])));
            """);
        Assert.Equal("""{"formKind":"login","username":"ana@exemplo.test","password":"Inventada-1!"}""", output);
    }

    [Fact]
    public void Signup_SendsTheNewPassword_AndChange_SendsBoth()
    {
        var output = CofreClassifierTests.Run("""
            const signup = C.submission({ formKind: 'signup', kinds: ['username', 'new-password', 'new-password'], user: 0 }, ['ana', 'Nova-2?', 'Nova-2?']);
            const change = C.submission({ formKind: 'change', kinds: ['password', 'new-password', 'new-password'], user: -1 }, ['Antiga-0!', 'Nova-2?', 'Nova-2?']);
            print(JSON.stringify(signup) + ' ' + JSON.stringify(change));
            """);
        Assert.Equal("""{"formKind":"signup","username":"ana","newPassword":"Nova-2?"} {"formKind":"change","username":"","password":"Antiga-0!","newPassword":"Nova-2?"}""", output);
    }

    // Nada: formulário que não é de palavras-passe, palavra-passe vazia, confirmação diferente, código de uma utilização.
    [Fact]
    public void Nothing_ForOtherForms_EmptyPasswords_MismatchedConfirmations_OrOneTimeCodes()
    {
        var output = CofreClassifierTests.Run("""
            print([
              C.submission({ formKind: 'address', kinds: ['postal-code'], user: -1 }, ['1000-001']),
              C.submission({ formKind: 'login', kinds: ['username', 'password'], user: 0 }, ['ana', '']),
              C.submission({ formKind: 'signup', kinds: ['username', 'new-password', 'new-password'], user: 0 }, ['ana', 'Nova-2?', 'Outra-3?']),
              C.submission({ formKind: 'other', kinds: ['one-time-code'], user: -1 }, ['123456']),
            ].map(x => JSON.stringify(x)).join(' '));
            """);
        Assert.Equal("null null null null", output);
    }

    // As restrições do campo para o gerador (C5): minlength, maxlength e pattern, só se a página as pôs.
    [Fact]
    public void Constraints_OnlyWhatThePageSet()
    {
        var output = CofreClassifierTests.Run("""
            print(JSON.stringify(C.constraints({ minLength: 8, maxLength: 16, pattern: '[A-Za-z0-9]+' })) + ' ' +
                  JSON.stringify(C.constraints({ minLength: -1, maxLength: -1, pattern: '' })) + ' ' +
                  JSON.stringify(C.constraints({ minLength: 0, maxLength: 524288, pattern: null })));
            """);
        Assert.Equal("""{"minLength":8,"maxLength":16,"pattern":"[A-Za-z0-9]+"} {} {}""", output);
    }

    // A preferência «oferecer» entra no script quando o canal o prepara (desligada: nem envio nem gerador saem dele).
    [Fact]
    public void Script_CarriesTheOfferFlag()
    {
        Assert.Contains("})(\"eabc\", true);", VaultScripts.Cofre("eabc", offer: true));
        Assert.Contains("})(\"eabc\", false);", VaultScripts.Cofre("eabc", offer: false));
        Assert.DoesNotContain(VaultScripts.OfferPlaceholder, VaultScripts.Cofre("eabc"));
    }

    // O navegador pode perguntar se a página nova tem outra vez um início de sessão (erro de login).
    [Fact]
    public void Probe_IsACallTheBrowserMayMake()
        => Assert.Contains("__cofre.probe(null)", VaultChannel.CallExpression("probe", null));
}
