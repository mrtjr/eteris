using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Tests.Sync;

// O que o computador novo mostra (0.2.15, T7): o cartão do pedido e o alerta «conta substituída». Decisão pura.
public class GuestPairingModelTests
{
    private static GuestPairingView From(SyncStatus? status) => GuestPairingModel.From(status);

    [Fact]
    public void None_WithoutStatus_OrWithNothingToShow()
    {
        Assert.Equal(GuestPairingView.None, From(null));
        Assert.Equal(GuestPairingKind.None, From(TestStatus.Of(SyncPhase.Off)).Kind);
        Assert.Equal(GuestPairingKind.None, From(TestStatus.Of(SyncPhase.On)).Kind);
        Assert.Equal(GuestPairingKind.None, From(TestStatus.Of(SyncPhase.Stopped)).Kind);
    }

    [Fact]
    public void Replaced_InChoose_AndWhenStopped()
    {
        Assert.Equal(GuestPairingKind.Replaced, From(TestStatus.Of(SyncPhase.Choose) with { ChainReplaced = true }).Kind);
        Assert.Equal(GuestPairingKind.Replaced, From(TestStatus.Of(SyncPhase.Stopped) with { ChainReplaced = true }).Kind);
        Assert.Null(From(TestStatus.Of(SyncPhase.Choose) with { ChainReplaced = true }).Code);
    }

    // «Não fui eu» esconde a barra do alerta (as Definições continuam a mostrá-lo).
    [Fact]
    public void NotMe_HidesTheAlertBar()
    {
        var replaced = new GuestPairingView(GuestPairingKind.Replaced);
        Assert.True(GuestPairingModel.ReplacedWants(replaced, dismissed: false));
        Assert.False(GuestPairingModel.ReplacedWants(replaced, dismissed: true));
        Assert.False(GuestPairingModel.ReplacedWants(GuestPairingView.None, dismissed: false));
        // O alerta não é o cartão: recolher o cartão não o esconde, e o cartão nunca o mostra.
        Assert.False(GuestPairingModel.CardWants(replaced, cardOpen: true));
        Assert.True(GuestPairingModel.Handles(replaced, cardOpen: false));
        Assert.False(GuestPairingModel.CardWants(GuestPairingView.None, cardOpen: true));
        Assert.False(GuestPairingModel.Handles(GuestPairingView.None, cardOpen: true));
    }

    // ---- o cartão (b2) ----------------------------------------------------------------------------------------------

    private static GuestPairing Pending(string? code = null, bool confirmed = false, string? error = null, GuestEnd end = GuestEnd.None, bool answered = false)
        => new(code, confirmed, error) { End = end, HostAnswered = answered };

    [Fact]
    public void Waiting_WhileNobodyAnswered()
    {
        Assert.Equal(new GuestPairingView(GuestPairingKind.Waiting), From(TestStatus.Of(SyncPhase.Pairing, pairing: Pending())));
        Assert.Equal(GuestPairingKind.Waiting, From(TestStatus.Of(SyncPhase.Pairing, pairing: Pending(answered: true))).Kind);
        Assert.Equal(GuestPairingKind.Waiting, From(TestStatus.Of(SyncPhase.Pairing)).Kind);
    }

    [Fact]
    public void Code_WhenTheOtherAnswered_AndConfirmed()
    {
        Assert.Equal(new GuestPairingView(GuestPairingKind.Code, "123 456"), From(TestStatus.Of(SyncPhase.Pairing, pairing: Pending("123 456", answered: true))));
        Assert.Equal(new GuestPairingView(GuestPairingKind.Code, "123 456", Confirmed: true),
            From(TestStatus.Of(SyncPhase.Pairing, pairing: Pending("123 456", confirmed: true, answered: true))));
    }

    [Fact]
    public void Joined_UntilTheFirstRoundEnds_AndNotWithAnError()
    {
        Assert.Equal(GuestPairingKind.Joined, From(TestStatus.Of(SyncPhase.On, running: true) with { JoinedNow = true }).Kind);
        Assert.Equal(GuestPairingKind.None, From(TestStatus.Of(SyncPhase.On, error: "Sem ligação") with { JoinedNow = true }).Kind);
        Assert.Equal(GuestPairingKind.None, From(TestStatus.Of(SyncPhase.On, running: true)).Kind);
    }

    [Fact]
    public void Ends_TimeUp_StillWaiting_Refused_Failed()
    {
        Assert.Equal(new GuestPairingView(GuestPairingKind.TimeUp),
            From(TestStatus.Of(SyncPhase.Choose, pairing: Pending("123 456", error: "O emparelhamento expirou. Comece de novo.", end: GuestEnd.TimeUp, answered: true))));
        Assert.Equal(new GuestPairingView(GuestPairingKind.StillWaiting),
            From(TestStatus.Of(SyncPhase.Choose, pairing: Pending(error: "Nenhum aparelho respondeu em 10 minutos. Comece de novo.", end: GuestEnd.StillWaiting))));
        Assert.Equal(new GuestPairingView(GuestPairingKind.Refused, Error: "O outro aparelho recusou o pedido."),
            From(TestStatus.Of(SyncPhase.Choose, pairing: Pending("123 456", error: "O outro aparelho recusou o pedido.", end: GuestEnd.Refused))));
        Assert.Equal(new GuestPairingView(GuestPairingKind.Failed, Error: "Sem ligação ao Google Drive. O Éteris tenta outra vez sozinho."),
            From(TestStatus.Of(SyncPhase.Choose, pairing: Pending(error: "Sem ligação ao Google Drive. O Éteris tenta outra vez sozinho.", end: GuestEnd.Failed))));
    }

    // Um computador novo em «escolher» sem pedido (depois de reabrir o Éteris, ou de «Não»): «Ainda à espera?».
    [Fact]
    public void StillWaiting_ForANewComputer_WithoutARequest()
    {
        Assert.Equal(GuestPairingKind.StillWaiting, From(TestStatus.Of(SyncPhase.Choose) with { MayAutoPair = true }).Kind);
        Assert.Equal(GuestPairingKind.StillWaiting, From(TestStatus.Of(SyncPhase.Choose, pairing: Pending()) with { MayAutoPair = true }).Kind);
        // Um computador que já teve conta (perdeu a criação ao mesmo tempo…) fica com o botão de sempre.
        Assert.Equal(GuestPairingKind.None, From(TestStatus.Of(SyncPhase.Choose)).Kind);
    }

    // O código só existe na vista em «Code», mesmo que o estado traga um.
    [Fact]
    public void Waiting_HasNoCode()
    {
        foreach (var status in new[]
                 {
                     TestStatus.Of(SyncPhase.Choose, pairing: Pending("123 456", error: "x", end: GuestEnd.TimeUp, answered: true)),
                     TestStatus.Of(SyncPhase.Choose, pairing: Pending("123 456", error: "x", end: GuestEnd.Refused)),
                     TestStatus.Of(SyncPhase.Choose, pairing: Pending("123 456", error: "x", end: GuestEnd.Failed)),
                     TestStatus.Of(SyncPhase.Choose, pairing: Pending("123 456")) with { ChainReplaced = true },
                     TestStatus.Of(SyncPhase.On, running: true, pairing: Pending("123 456")) with { JoinedNow = true },
                 })
        {
            var view = From(status);
            Assert.NotEqual(GuestPairingKind.Code, view.Kind);
            Assert.Null(view.Code);
        }
    }

    [Fact]
    public void Replaced_ComesFirst()
        => Assert.Equal(GuestPairingKind.Replaced,
            From(TestStatus.Of(SyncPhase.Choose, pairing: Pending("123 456", error: "x", end: GuestEnd.TimeUp, answered: true)) with { ChainReplaced = true, MayAutoPair = true }).Kind);

    [Fact]
    public void Wants_Table()
    {
        foreach (var kind in Enum.GetValues<GuestPairingKind>())
        {
            var view = new GuestPairingView(kind);
            var card = kind is not (GuestPairingKind.None or GuestPairingKind.Replaced);
            Assert.Equal(card, GuestPairingModel.CardWants(view, cardOpen: true));
            Assert.False(GuestPairingModel.CardWants(view, cardOpen: false));
            Assert.Equal(kind == GuestPairingKind.Replaced, GuestPairingModel.ReplacedWants(view, dismissed: false));
            Assert.False(GuestPairingModel.ReplacedWants(view, dismissed: true));
            Assert.Equal(card || kind == GuestPairingKind.Replaced, GuestPairingModel.Handles(view, cardOpen: true));
            Assert.Equal(kind == GuestPairingKind.Replaced, GuestPairingModel.Handles(view, cardOpen: false));
        }
    }

    [Fact]
    public void Sim_CountsTwoSecondsAfterTheCodeAppears()
    {
        Assert.Equal(2_000, GuestPairingModel.ConfirmDelayMs);
        Assert.False(GuestPairingModel.MayConfirm(1_000, 2_999));
        Assert.True(GuestPairingModel.MayConfirm(1_000, 3_000));
    }

    // Todos os textos do computador novo, à letra do plano (P2 e «Fechar» são propostas para o dono); os mesmos na página.
    [Fact]
    public void Texts_ToTheLetter()
    {
        Assert.Equal("Falta um passo no outro computador.", GuestPairingModel.WaitTitle);
        Assert.Equal("Abra o Éteris num computador onde já entrou. Este ecrã mostra um código quando o outro responder.", GuestPairingModel.WaitText);
        Assert.Equal("Escreva este código no outro computador:", GuestPairingModel.TypeThisCode);
        Assert.Equal("Escreveu 123 456 no outro computador e ele aceitou?", GuestPairingModel.Ask("123 456"));
        Assert.Equal("Sim", GuestPairingModel.Yes);
        Assert.Equal("Não", GuestPairingModel.No);
        Assert.Equal("À espera da aprovação no outro computador…", GuestPairingModel.WaitingApproval);
        Assert.Equal("Este computador entrou na conta. A sincronizar…", GuestPairingModel.Joined);
        Assert.Equal("O tempo acabou.", GuestPairingModel.TimeUp);
        Assert.Equal("Ainda à espera?", GuestPairingModel.StillWaiting);
        Assert.Equal("Pedir outra vez", GuestPairingModel.AskAgain);
        Assert.Equal("O Éteris tem de estar aberto no outro computador.", GuestPairingModel.MustBeOpen);
        Assert.Equal("Não tenho o outro computador à mão", GuestPairingModel.NoOther);
        Assert.Equal("Para entrar sem o outro computador, use o seu kit de recuperação (24 palavras ou o código de 56 caracteres).", GuestPairingModel.UseKit);
        Assert.Equal("Colar o kit", GuestPairingModel.PasteKit);
        var html = SettingsPage.Render(new UserSettings());
        // O que a página mostra a negrito vem entre marcas: compara-se o texto sem elas.
        var plain = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
        foreach (var text in new[]
                 {
                     GuestPairingModel.WaitTitle, GuestPairingModel.WaitText, GuestPairingModel.TypeThisCode, " no outro computador e ele aceitou?",
                     GuestPairingModel.Yes, GuestPairingModel.No, GuestPairingModel.WaitingApproval, GuestPairingModel.Joined, GuestPairingModel.TimeUp,
                     GuestPairingModel.StillWaiting, GuestPairingModel.AskAgain, GuestPairingModel.MustBeOpen, GuestPairingModel.NoOther,
                     GuestPairingModel.UseKit, GuestPairingModel.PasteKit,
                 })
            Assert.Contains(text, plain, StringComparison.Ordinal);
        Assert.DoesNotContain("O outro computador mostra este código", html, StringComparison.Ordinal);
    }

    // «Começar do zero…» não é desta tarefa (T12); a saída que já existe fica. (As 24 palavras entraram com a T5d.)
    [Fact]
    public void StartOver_IsNotOfferedByThisTask()
    {
        var root = GoogleSyncTests.FindRoot();
        var page = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "Eteris.Browser", "Browser", "settings.html"));
        var model = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "src", "Eteris.Browser", "Sync", "GuestPairingModel.cs"));
        foreach (var text in new[] { page, model })
        {
            Assert.DoesNotContain("Começar do zero", text, StringComparison.Ordinal);
        }
        Assert.Contains("syncLostLink", page, StringComparison.Ordinal);
        Assert.Contains("Perdeu o código e não tem outro computador ligado?", page, StringComparison.Ordinal);
    }

    // Os textos do alerta, à letra (P2 e «Fechar» são propostas para o dono), e os mesmos na página das Definições.
    [Fact]
    public void ReplacedTexts_ToTheLetter()
    {
        Assert.Equal("A conta de sincronização na nuvem foi apagada ou substituída noutro computador.", GuestPairingModel.ReplacedTitle);
        Assert.Equal("Se não foi você, alguém pode ter entrado na sua conta Google: mude a palavra-passe da Google e use «Retirar o acesso do Éteris». Os dados deste computador estão intactos.",
            GuestPairingModel.ReplacedText);
        Assert.Equal("Foi eu — juntar este computador", GuestPairingModel.ReplacedJoin);
        Assert.Equal("Não fui eu", GuestPairingModel.ReplacedNotMe);
        Assert.Equal("Se juntar este computador, as palavras-passe e os outros dados dele passam a ser enviados para a conta que está agora na nuvem.",
            GuestPairingModel.ReplacedConsequence);
        Assert.Equal("Fechar", GuestPairingModel.Close);
        var html = SettingsPage.Render(new UserSettings());
        foreach (var text in new[] { GuestPairingModel.ReplacedTitle, GuestPairingModel.ReplacedText, GuestPairingModel.ReplacedJoin,
                     GuestPairingModel.ReplacedNotMe, GuestPairingModel.ReplacedConsequence })
            Assert.Contains(text, html, StringComparison.Ordinal);
    }
}
