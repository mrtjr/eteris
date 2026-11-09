using System.IO;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C4 (docs/palavras-passe.md): guardar, atualizar e «Nunca neste site». O candidato vive só na memória e com prazo; só
// se oferece depois de um sucesso provável (navegação do frame principal sem o início de sessão voltar, ou o formulário
// a desaparecer); nunca na janela privada, num código de uma utilização, numa palavra-passe gigante, num site da lista
// «nunca» ou quando já está guardada igual.
public class SaveFlowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly VaultContext Ctx = new(7, "F1", "L1", "https://conta.exemplo.test", IsMain: true);

    private static VaultMessage Submit(string formKind, string? user = "ana@exemplo.test", string? password = "Inventada-1!", string? newPassword = null)
        => new(VaultMessageType.Submit) { Form = "m1", FormKind = formKind, Username = user, Password = password, NewPassword = newPassword };

    private static SaveCandidate Candidate(string formKind = "login", string? user = "ana@exemplo.test", string? password = "Inventada-1!", string? newPassword = null)
        => SavePolicy.Candidate(Submit(formKind, user, password, newPassword), Ctx, T0, offerEnabled: true, isPrivate: false)!;

    // ---- O candidato ----

    [Fact]
    public void Candidate_KeepsTheBrowserOrigin_AndTheValues()
    {
        var c = Candidate();
        Assert.Equal("https://conta.exemplo.test", c.Origin);
        Assert.Equal("ana@exemplo.test", c.Username);
        Assert.Equal("Inventada-1!", c.Password);
        Assert.Equal("m1", c.Form);
        Assert.Equal(7, c.ContextId);
        Assert.DoesNotContain("Inventada", c.ToString());
        Assert.DoesNotContain("ana@", c.ToString());
    }

    [Theory]
    [InlineData(false, false)]   // «Oferecer para guardar palavras-passe» desligado
    [InlineData(true, true)]     // janela privada (D-3)
    public void NoCandidate_WhenOfferingIsOff_OrInAPrivateWindow(bool offer, bool isPrivate)
        => Assert.Null(SavePolicy.Candidate(Submit("login"), Ctx, T0, offer, isPrivate));

    [Theory]
    [InlineData("other")]
    [InlineData("address")]
    [InlineData("payment")]
    public void NoCandidate_OutsideLoginSignupOrChange(string formKind)
        => Assert.Null(SavePolicy.Candidate(Submit(formKind), Ctx, T0, true, false));

    [Fact]
    public void NoCandidate_WithoutAPassword_OrWithAGiantOne()
    {
        Assert.Null(SavePolicy.Candidate(Submit("login", password: ""), Ctx, T0, true, false));
        Assert.Null(SavePolicy.Candidate(Submit("login", password: null), Ctx, T0, true, false));
        Assert.Null(SavePolicy.Candidate(Submit("login", password: new string('x', SavePolicy.MaxPassword + 1)), Ctx, T0, true, false));
        Assert.NotNull(SavePolicy.Candidate(Submit("login", password: new string('x', SavePolicy.MaxPassword)), Ctx, T0, true, false));
        Assert.Null(SavePolicy.Candidate(Submit("signup", newPassword: null), Ctx, T0, true, false));
    }

    // Só páginas http(s): eteris://, file:// e data: nunca têm candidato.
    [Theory]
    [InlineData("eteris://passwords")]
    [InlineData("file:///C:/x.html")]
    [InlineData("data:text/html,x")]
    public void NoCandidate_OutsideTheWeb(string origin)
        => Assert.Null(SavePolicy.Candidate(Submit("login"), Ctx with { Origin = origin }, T0, true, false));

    // Só na memória e com prazo: 5 minutos.
    [Fact]
    public void Candidate_ExpiresAfterFiveMinutes()
    {
        var c = Candidate();
        Assert.False(SavePolicy.Expired(c, T0 + TimeSpan.FromMinutes(4.9)));
        Assert.True(SavePolicy.Expired(c, T0 + TimeSpan.FromMinutes(5.1)));
    }

    [Fact]
    public void Candidate_NeverReachesTheVaultFiles()
    {
        var root = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Vault");
        foreach (var file in new[] { "VaultService.cs", "VaultStore.cs", "VaultKey.cs" })
            Assert.DoesNotContain("SaveCandidate", File.ReadAllText(Path.Combine(root, file)));
    }

    // ---- Sucesso provável ----

    // Navegação do frame principal: correu bem, a não ser que o início de sessão volte na mesma origem (erro).
    [Fact]
    public void Navigation_IsSuccess_UnlessTheLoginFormComesBack()
    {
        var c = Candidate();
        Assert.Equal(SubmitOutcome.Success, SavePolicy.AfterNavigation(c, "https://conta.exemplo.test", loginFormAgain: false, T0.AddSeconds(2)));
        Assert.Equal(SubmitOutcome.Failed, SavePolicy.AfterNavigation(c, "https://conta.exemplo.test", loginFormAgain: true, T0.AddSeconds(2)));
        // Outro site (um SSO, a página da conta noutro domínio): correu bem mesmo com outro início de sessão lá.
        Assert.Equal(SubmitOutcome.Success, SavePolicy.AfterNavigation(c, "https://outro.exemplo.test", loginFormAgain: true, T0.AddSeconds(2)));
        // Página que não é http(s) (PDF, eteris://): correu bem.
        Assert.Equal(SubmitOutcome.Success, SavePolicy.AfterNavigation(c, null, loginFormAgain: false, T0.AddSeconds(2)));
    }

    // Uma navegação muito depois do envio (o utilizador desistiu e foi a outra página) não conta.
    [Fact]
    public void Navigation_LongAfterTheSubmit_DropsTheCandidate()
        => Assert.Equal(SubmitOutcome.Failed, SavePolicy.AfterNavigation(Candidate(), "https://conta.exemplo.test", false, T0 + SavePolicy.SuccessWindow + TimeSpan.FromSeconds(1)));

    // O formulário desapareceu (SPA): correu bem, se for o formulário do candidato e no mesmo documento.
    [Fact]
    public void FormGone_IsSuccess_OnlyForTheCandidateForm()
    {
        var c = Candidate();
        Assert.Equal(SubmitOutcome.Success, SavePolicy.AfterFormGone(c, contextId: 7, form: "m1", T0.AddSeconds(1)));
        Assert.Equal(SubmitOutcome.Pending, SavePolicy.AfterFormGone(c, contextId: 7, form: "m2", T0.AddSeconds(1)));
        Assert.Equal(SubmitOutcome.Pending, SavePolicy.AfterFormGone(c, contextId: 8, form: "m1", T0.AddSeconds(1)));
        Assert.Equal(SubmitOutcome.Failed, SavePolicy.AfterFormGone(c, 7, "m1", T0 + SavePolicy.SuccessWindow + TimeSpan.FromSeconds(1)));
    }

    // ---- O que oferecer ----

    private static StoredLogin Stored(string user, string password, bool generated = false, string id = "id1") => new(id, user, password, generated);

    [Fact]
    public void NothingSaved_OffersToSave()
    {
        var offer = SavePolicy.Decide(Candidate(), neverSite: false, []);
        Assert.Equal(SaveOfferKind.Save, offer.Kind);
        Assert.Equal("ana@exemplo.test", offer.Username);
        Assert.Equal("Inventada-1!", offer.Password);
        Assert.Null(offer.ExistingId);
        Assert.Equal("Guardar a palavra-passe de conta.exemplo.test?", SavePolicy.Question(offer));
        Assert.DoesNotContain("Inventada", offer.ToString());
    }

    [Fact]
    public void SameAsSaved_OffersNothing()
        => Assert.Equal(SaveOfferKind.None, SavePolicy.Decide(Candidate(), false, [Stored("ana@exemplo.test", "Inventada-1!")]).Kind);

    [Fact]
    public void NeverSite_OffersNothing()
        => Assert.Equal(SaveOfferKind.None, SavePolicy.Decide(Candidate(), neverSite: true, []).Kind);

    [Fact]
    public void SameUserWithAnotherPassword_OffersToUpdate()
    {
        var offer = SavePolicy.Decide(Candidate(), false, [Stored("bruno@exemplo.test", "x"), Stored("ana@exemplo.test", "Antiga-0!", id: "ana")]);
        Assert.Equal(SaveOfferKind.Update, offer.Kind);
        Assert.Equal("ana", offer.ExistingId);
        Assert.Equal("Atualizar a palavra-passe de ana@exemplo.test em conta.exemplo.test?", SavePolicy.Question(offer));
    }

    [Fact]
    public void AnotherUser_OffersToSaveANewOne()
        => Assert.Equal(SaveOfferKind.Save, SavePolicy.Decide(Candidate(), false, [Stored("bruno@exemplo.test", "Inventada-1!")]).Kind);

    // Criação de conta: guarda a nova palavra-passe.
    [Fact]
    public void Signup_SavesTheNewPassword()
    {
        var offer = SavePolicy.Decide(Candidate("signup", password: null, newPassword: "Nova-Inventada-2?"), false, []);
        Assert.Equal(SaveOfferKind.Save, offer.Kind);
        Assert.Equal("Nova-Inventada-2?", offer.Password);
    }

    // Mudança de palavra-passe (muitas vezes sem utilizador no formulário): a guardada é a que tem a palavra-passe atual.
    [Fact]
    public void Change_UpdatesTheOneWithTheCurrentPassword()
    {
        var c = Candidate("change", user: "", password: "Antiga-0!", newPassword: "Nova-Inventada-2?");
        var offer = SavePolicy.Decide(c, false, [Stored("bruno@exemplo.test", "Outra-9"), Stored("ana@exemplo.test", "Antiga-0!", id: "ana")]);
        Assert.Equal(SaveOfferKind.Update, offer.Kind);
        Assert.Equal("ana", offer.ExistingId);
        Assert.Equal("ana@exemplo.test", offer.Username);
        Assert.Equal("Nova-Inventada-2?", offer.Password);
    }

    // Sem saber qual, com uma só guardada no site: é essa. Já com a nova: nada.
    [Fact]
    public void Change_WithASingleSavedLogin_UpdatesIt_AndNothingIfAlreadyNew()
    {
        var c = Candidate("change", user: "", password: "Esquecida", newPassword: "Nova-Inventada-2?");
        Assert.Equal(SaveOfferKind.Update, SavePolicy.Decide(c, false, [Stored("ana@exemplo.test", "Antiga-0!", id: "ana")]).Kind);
        Assert.Equal(SaveOfferKind.None, SavePolicy.Decide(c, false, [Stored("ana@exemplo.test", "Nova-Inventada-2?")]).Kind);
    }

    // Uma palavra-passe gerada à espera de confirmação (C5): o envio com sucesso confirma-a, sem perguntar.
    [Fact]
    public void GeneratedPassword_IsConfirmed_WithTheUserFromTheForm()
    {
        var c = Candidate("signup", user: "ana@exemplo.test", password: null, newPassword: "Gerada-Xk7aQ9");
        var offer = SavePolicy.Decide(c, false, [Stored("", "Gerada-Xk7aQ9", generated: true, id: "g1")]);
        Assert.Equal(SaveOfferKind.ConfirmGenerated, offer.Kind);
        Assert.Equal("g1", offer.ExistingId);
        Assert.Equal("ana@exemplo.test", offer.Username);
    }

    // Numa mudança de palavra-passe sem utilizador no formulário, a gerada é da conta que tinha a palavra-passe atual.
    [Fact]
    public void GeneratedPassword_OnAChangeForm_BelongsToTheAccountWithTheCurrentPassword()
    {
        var c = Candidate("change", user: "", password: "Antiga-0!", newPassword: "Gerada-Xk7aQ9");
        var offer = SavePolicy.Decide(c, false, [Stored("ana@exemplo.test", "Antiga-0!", id: "ana"), Stored("", "Gerada-Xk7aQ9", generated: true, id: "g1")]);
        Assert.Equal(SaveOfferKind.ConfirmGenerated, offer.Kind);
        Assert.Equal("g1", offer.ExistingId);
        Assert.Equal("ana@exemplo.test", offer.Username);
    }

    // Revisão: uma gerada que já tem utilizador (gerada para uma conta que existe) confirma-se sem perguntar só com esse
    // utilizador; outro, vindo do formulário, pergunta na bolha (nunca muda o utilizador em silêncio).
    [Fact]
    public void GeneratedPassword_WithAnotherUsernameInTheForm_IsAskedNotConfirmedSilently()
    {
        var same = SavePolicy.Decide(Candidate("change", user: "ana@exemplo.test", password: null, newPassword: "Gerada-Xk7aQ9"), false,
            [Stored("ana@exemplo.test", "Gerada-Xk7aQ9", generated: true, id: "g1")]);
        Assert.Equal(SaveOfferKind.ConfirmGenerated, same.Kind);
        var other = SavePolicy.Decide(Candidate("change", user: "outra@exemplo.test", password: null, newPassword: "Gerada-Xk7aQ9"), false,
            [Stored("ana@exemplo.test", "Gerada-Xk7aQ9", generated: true, id: "g1")]);
        Assert.Equal(SaveOfferKind.Save, other.Kind);
        Assert.Equal("outra@exemplo.test", other.Username);
        Assert.Null(other.ExistingId);
    }

    // O ícone de chave fica até à próxima navegação; a que trouxe a oferta (e um redirecionamento logo a seguir) não conta.
    [Fact]
    public void Offer_SurvivesOnlyTheNavigationsRightAfterIt()
    {
        Assert.True(SavePolicy.KeepsOfferAcrossNavigation(TimeSpan.FromSeconds(1)));
        Assert.False(SavePolicy.KeepsOfferAcrossNavigation(SavePolicy.RedirectGrace + TimeSpan.FromSeconds(1)));
    }
}
