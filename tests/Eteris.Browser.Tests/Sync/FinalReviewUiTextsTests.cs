using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests.Sync;

// A revisão final da 0.2.15, grupo «iu-textos» (final-review.md, I-01…I-12): o que as revisões por tarefa não viam na
// interface e nos textos públicos. Testes de fonte e de modelos; nada aqui abre janelas, toca na rede ou no perfil do dono.
public class FinalReviewUiTextsTests
{
    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts])).Replace("\r\n", "\n");

    private static string Body(string text, string start, string end)
    {
        var at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "não encontrei " + start);
        var stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        Assert.True(stop >= 0, "não encontrei o fim " + end);
        return text[at..stop];
    }

    // ---- I-01: as palavras do kit e uma seleção numa página interna não saem pelo menu ---------------------------------

    [Fact]
    public void I01_KitWords_CannotBeSelected()
    {
        var css = Regex.Match(Source("Browser", "settings.html"), @"\.kitwords \{[^}]*\}").Value;
        Assert.Contains("user-select: none", css);
    }

    [Fact]
    public void I01_SearchSelection_OnlyOnWebPages()
    {
        var inner = PageMenu.Build(new PageContext(null, null, "abacate afivelar", false, false, false, false, IsWebPage: false), "DuckDuckGo");
        Assert.DoesNotContain(inner, i => i?.Command == PageCommand.SearchSelection);
        var innerField = PageMenu.Build(new PageContext(null, null, "segredo", true, false, false, false, IsWebPage: false), "DuckDuckGo");
        Assert.DoesNotContain(innerField, i => i?.Command == PageCommand.SearchSelection);
        var web = PageMenu.Build(new PageContext(null, null, "abacate afivelar", false, false, false, false, IsWebPage: true), "DuckDuckGo");
        Assert.Contains(web, i => i?.Command == PageCommand.SearchSelection);
    }

    // ---- I-02: uma janela fechada não volta a mostrar barras; um «wants» que lança não derruba as outras --------------

    [Fact]
    public void I02_WantsThatThrows_CountsAsFalse_AndTheOthersStillDecide()
    {
        var set = new WindowBarSet();
        var shown = new List<(WindowBar, bool)>();
        set.Register(WindowBar.PairingRequest, () => throw new InvalidOperationException("Collection was modified"), on => shown.Add((WindowBar.PairingRequest, on)));
        set.Register(WindowBar.KitReminder, () => true, on => shown.Add((WindowBar.KitReminder, on)));
        Assert.Equal(WindowBar.KitReminder, set.Refresh());
        Assert.Contains((WindowBar.PairingRequest, false), shown);
        Assert.Contains((WindowBar.KitReminder, true), shown);
    }

    [Fact]
    public void I02_RefreshBars_StopsOnceTheWindowClosed()
    {
        var bars = Source("MainWindow.SignInBar.cs");
        var refresh = Body(bars, "private void RefreshBars()", "\n    }");
        Assert.Matches(@"private void RefreshBars\(\)\s*\{\s*if \(_barsClosed\) return;", refresh);
        // Posto no Closed, registado no construtor (antes de qualquer outro Closed das barras), também nas privadas.
        var init = Body(Source("MainWindow.PairingBar.cs"), "private void InitPairingBar()", "if (IsPrivate) return;");
        Assert.Contains("Closed += (_, _) => _barsClosed = true;", init);
    }

    [Fact]
    public void I02_TheOverlay_IsForgottenWhenItCloses()
    {
        var overlay = Source("MainWindow.Overlay.cs");
        Assert.DoesNotContain("Closed += (_, _) => _overlay?.Close();", overlay);
        Assert.Matches(@"Closed \+= \(_, _\) =>\s*\{\s*var closing = _overlay;\s*_overlay = null;\s*closing\?\.Close\(\);", overlay);
        Assert.Contains("if (_overlay is null || _overlayRoot is null) return;", Body(overlay, "private void UpdateOverlayVisibility()", "var needed"));
    }

    [Theory]
    [InlineData("MainWindow.PairingBar.cs", "private async void OnPairingCode(")]
    [InlineData("MainWindow.GuestPairing.cs", "private async void OnReplacedJoin()")]
    [InlineData("MainWindow.SignInBar.cs", "private async void AnswerSecretsOfferFromBar(")]
    public void I02_TheAsyncBarHandlers_CheckTheWindowAfterTheWait(string file, string start)
    {
        var body = Body(Source(file), start, "\n    }\n");
        // Depois do «await», nada toca na janela (barras, notas, avisos) antes da guarda; os «catch» só guardam o resultado.
        var afterAwait = body[body.IndexOf("await ", StringComparison.Ordinal)..];
        var guard = afterAwait.IndexOf("if (_barsClosed) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0, file + ": sem a guarda depois do await");
        Assert.DoesNotMatch(@"RefreshBars|ShowToast|ShowNote|AfterPairingAction|AfterGuestAction", afterAwait[..guard]);
    }

    // ---- I-04: o pedido de permissão e o procurar empurram as três barras, não só a do início de sessão --------------

    [Fact]
    public void I04_PermissionAndFind_PlaceAllThreeBars()
    {
        var bars = Source("MainWindow.SignInBar.cs");
        var place = Body(bars, "private void PlaceBarsBelowPageBars()", "\n    }");
        Assert.Contains("PlaceSignInBar(_signInBarMargins);", place);
        Assert.Contains("PlacePairingBar(_pairingBarMargins);", place);
        Assert.Contains("PlaceGuestBar(_guestBarMargins);", place);
        var wire = Body(bars, "private void WireSignInBar()", "\n    }");
        Assert.Contains("other.SizeChanged += (_, _) => PlaceBarsBelowPageBars();", wire);
        Assert.Contains("other.IsVisibleChanged += (_, _) => PlaceBarsBelowPageBars();", wire);
        Assert.Contains("new FrameworkElement[] { PermissionBar, FindBar }", wire);
        Assert.DoesNotContain("SizeChanged += (_, _) => PlaceSignInBar(", wire);
    }

    // ---- I-05: a captura do próprio Éteris não grava o kit nem uma palavra-passe à vista -------------------------------

    [Fact]
    public void I05_OwnScreenshot_RefusesWhileASecretIsOnScreen()
    {
        var capture = Body(Source("MainWindow.Screenshot.cs"), "private async void CapturePage(bool fullPage)", "_capturing = true;");
        var guard = capture.IndexOf("if (Infrastructure.CaptureShield.SecretVisible)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "sem a guarda do segredo à vista");
        Assert.Contains("ShowToast(\"Há um segredo à vista: a captura não foi feita.\");", capture[guard..]);
        // O kit à vista segura o mesmo segredo (SecretOnScreen → CaptureShield.HoldSecret).
        Assert.Contains("Vault.SecretOnScreen.Show(", Source("Browser", "EterisSchemeHandlerFactory.cs"));
    }

    // ---- I-06: os textos e as ligações levam ao item, mesmo dentro de «Mais opções» ----------------------------------

    [Fact]
    public void I06_SyncOffText_PointsIntoMoreOptions()
    {
        var page = Source("Browser", "settings.html");
        Assert.DoesNotContain("«Apagar os meus dados…», mais abaixo", page);
        var off = Body(page, "const deleteStuck = ", "$('syncStart').textContent");
        Assert.Contains("«Mais opções» › «Apagar os meus dados…»", off);
        Assert.Contains("toAnchor('#syncDeleteItem')", off);
        // Um destino dentro de «Mais opções» que está escondido leva, pelo menos, à secção.
        var go = Body(page, "const goToAnchor = () => {", "const toAnchor = ");
        Assert.Contains("else $('sincronizacao').scrollIntoView();", go);
    }

    [Fact]
    public void I02_SyncData_ReadsTheStatusGuarded()
    {
        var page = Body(Source("Browser", "SettingsPage.cs"), "internal static object SyncData(bool isPrivate)", "\n    }\n");
        Assert.Contains("Sync.SyncService.Guarded(() => Sync.SyncService.Status, null)", page);
        Assert.DoesNotContain("StatusData(Sync.SyncService.Status,", page);
    }
}
