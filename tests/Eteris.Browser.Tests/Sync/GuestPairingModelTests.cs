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
