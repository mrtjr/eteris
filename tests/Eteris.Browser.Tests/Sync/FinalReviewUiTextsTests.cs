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

    // ---- I-07: o ficheiro do kit e a área de transferência segura -------------------------------------------------

    [Fact]
    public void I07_SavingTheFile_MarksTheKitThatWasSaved()
    {
        var routes = Source("Browser", "EterisSchemeHandlerFactory.cs");
        Assert.DoesNotContain("MarkKitSaved())", routes);
        Assert.Equal(2, Regex.Matches(routes, Regex.Escape("Sync.SyncService.Agent()?.MarkKitSaved(kit.KitId)")).Count);
    }

    [Fact]
    public void I07_TheKitFile_IsFlushedToDiskBeforeCountingAsSaved()
    {
        var write = Body(Source("Sync", "RecoveryKit.cs"), "public static void WriteFile(string path, string html)", "\n    }");
        Assert.Contains("new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)", write);
        Assert.Contains("Flush(flushToDisk: true)", write);
        var folder = Path.Combine(Path.GetTempPath(), "eteris-i07-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "kit.html");
            File.WriteAllText(path, "antigo e mais comprido do que o novo");
            Eteris.Browser.Sync.RecoveryKit.WriteFile(path, "Kit é");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes("Kit é"), bytes); // sem BOM, e o resto do antigo não fica
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void I07_Clipboard_IsEmptiedWhenAnExclusionFormatFails()
    {
        var set = Body(Source("Vault", "SecureClipboard.cs"), "public bool SetText(string text, IReadOnlyList<(string Format, byte[] Data)> extra)", "\n    public bool Clear()");
        // Depois de o texto entrar, uma falha num formato de exclusão esvazia outra vez antes de devolver false.
        var afterText = set[set.IndexOf("Put(UnicodeText, bytes)", StringComparison.Ordinal)..];
        var failed = afterText.IndexOf("return false;", StringComparison.Ordinal);
        Assert.True(failed >= 0);
        Assert.Contains("EmptyClipboard();", afterText[..failed]);
        Assert.DoesNotMatch(@"if \(id == 0 \|\| !Put\(id, data\)\) return false;", set);
    }

    // ---- I-08: entrar com o kit na conta substituída passa pela mesma porta do Windows que «Fui eu» -----------------

    private static string RouteCase(string routes, string path)
    {
        var start = routes.IndexOf("case \"" + path + "\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "não encontrei a rota " + path);
        var end = routes.IndexOf("case \"", start + 6, StringComparison.Ordinal);
        return end < 0 ? routes[start..] : routes[start..end];
    }

    [Theory]
    [InlineData("/sync-kit-from-file")]
    [InlineData("/sync-join")]
    public void I08_KitRoutes_OnAReplacedAccount_AskWindowsFirst(string path)
    {
        var route = RouteCase(Source("Browser", "EterisSchemeHandlerFactory.cs"), path);
        var gate = route.IndexOf("await Sync.SyncService.MayJoinWithKitAsync(purpose => ConfirmWithWindows(browserId, purpose))", StringComparison.Ordinal);
        Assert.True(gate >= 0, path + ": sem a porta do Windows");
        var join = route.IndexOf("JoinWithCodeAsync(", StringComparison.Ordinal);
        Assert.True(join > gate, path + ": a entrada vem antes da porta");
        // Recusada, nada entra e a página recebe o estado (com a mensagem «replaced»).
        Assert.Contains("return Vault.VaultReply.Json(SettingsPage.SyncData(isPrivate: false));", route[gate..join]);
    }

    [Fact]
    public void I08_MayJoinWithKit_UsesTheReplacedGate_OnlyOnAReplacedAccount()
    {
        var service = Source("Sync", "SyncService.cs");
        var body = Body(service, "public static async Task<bool> MayJoinWithKitAsync(", "\n    }\n");
        Assert.Contains("!agent.Status.ChainReplaced) return true;", body);
        Assert.Contains("SyncChoices.MayJoinReplacedAsync(agent, confirm)", body);
        Assert.Contains("Say(\"replaced\", SyncChoices.NotConfirmed(\"este computador não entrou na conta\"));", body);
    }

    // ---- I-09: os textos do computador novo e do kit -------------------------------------------------------------

    [Fact]
    public void I09_FirstPerson_IsFuiEu_Everywhere()
    {
        var root = GoogleSyncTests.FindRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".html"))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Concat([Path.Combine(root, "docs", "sincronizacao.md"), Path.Combine(root, "docs", "palavras-passe.md")]);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains("Foi " + "eu", StringComparison.Ordinal), file);
            Assert.False(text.Contains("não foi juntado", StringComparison.Ordinal), file);
        }
        Assert.Equal("Fui eu — juntar este computador", GuestPairingModel.ReplacedJoin);
        Assert.Contains(">Fui eu — juntar este computador</button>", Source("Browser", "settings.html"));
        Assert.Contains("Content=\"Fui eu — juntar este computador\"", Source("Sync", "GuestPairingBar.xaml"));
    }

    [Fact]
    public void I09_StaleKit_DoesNotBlameARemovedComputer()
    {
        Assert.Equal("O seu kit de recuperação antigo deixou de valer (a conta mudou de chave). Guarde o novo.", RecoveryReminder.StaleText);
        Assert.Contains("const KIT_STALE = '" + RecoveryReminder.StaleText + "';", Source("Browser", "settings.html"));
    }

    [Fact]
    public void I09_ApproveWarning_IsGone()
        => Assert.DoesNotContain("ApproveWarning", Source("Sync", "SyncChoices.cs"));

    [Theory]
    [InlineData("MainWindow.PairingBar.cs", "private async void OnPairingCode(")]
    [InlineData("MainWindow.GuestPairing.cs", "private async void OnReplacedJoin()")]
    public void I09_TheBars_NeverShowARawExceptionMessage(string file, string start)
    {
        var body = Body(Source(file), start, "\n    }\n");
        var unexpected = body[body.IndexOf("catch (Exception)", StringComparison.Ordinal)..];
        Assert.Contains("SyncChoices.CouldNotFinish", unexpected[..unexpected.IndexOf('}')]);
        Assert.DoesNotContain("catch (Exception e)", body);
    }

    // ---- I-10: as três barras anunciam-se ao leitor de ecrã (LiveSetting sozinho não anuncia nada em WPF) -------------

    [Fact]
    public void I10_LiveRegion_CreatesThePeer_AndWaitsUntilVisible()
    {
        var helper = Source("Sync", "LiveRegion.cs");
        Assert.Contains("UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", helper);
        Assert.Contains("IsVisibleChanged += OnBecameVisible", helper);
        Assert.Contains("IsVisibleChanged -= OnBecameVisible", helper);
        Assert.DoesNotContain("FromElement(", helper);
    }

    [Theory]
    [InlineData("SignInBar.xaml.cs", "LiveRegion.Announce(Message)")]
    [InlineData("PairingBar.xaml.cs", "LiveRegion.Announce(TitleText)")]
    [InlineData("PairingBar.xaml.cs", "LiveRegion.Announce(NoteText)")]
    [InlineData("GuestPairingBar.xaml.cs", "LiveRegion.Announce(block)")]
    public void I10_TheBars_AnnounceWhatChanged(string file, string call)
    {
        var code = Source("Sync", file);
        Assert.Contains(call, code);
        Assert.DoesNotContain("UIElementAutomationPeer.FromElement(", code);
    }

    // ---- I-11: os botões que abrem as Definições numa âncora, a entrada pelas páginas do cofre e o «Sincronizado.» ----

    [Fact]
    public void I11_OpenInternal_WithAnAnchor_ReusesThePageAndGoesThere()
    {
        var open = Body(Source("MainWindow.PageMenu.cs"), "private void OpenInternal(string url)", "\n    }\n");
        Assert.Contains("var hash = url.IndexOf('#');", open);
        Assert.Contains("var page = url[..hash];", open);
        Assert.Contains("t.Address.StartsWith(page, StringComparison.OrdinalIgnoreCase)", open);
        // O mesmo endereço: carrega outra vez (o mesmo hash não volta a levar a vista ao item); outro: vai lá.
        Assert.Matches(@"string\.Equals\(open\.Address, url, StringComparison\.OrdinalIgnoreCase\)\)\s*ReloadActive\(\);\s*else LoadUrl\(url\);", open);
    }

    [Fact]
    public void I11_SignedInBar_StopsTheHideTimer_WhenTheNextViewHasNone()
    {
        var show = Body(Source("MainWindow.SignInBar.cs"), "private void ShowSignInBar(bool on)", "\n    }\n");
        Assert.Matches(@"if \(view\.HideAfter is not \{ \} hold\)\s*\{\s*(//[^\n]*\n\s*)*_signInBarHide\?\.Stop\(\);\s*_signInTimed = null;", show);
    }

    [Theory]
    [InlineData("VaultRoutes.cs")]
    [InlineData("VaultRoutes.Autofill.cs")]
    public void I11_SignInRoute_ChecksTheOfferAgain(string file)
    {
        var route = Body(Source("Vault", file), "case \"/sign-in\":", "return VaultReply.Json(new { ok = true });");
        Assert.Contains("if (!host.OffersSignIn) return VaultReply.Json(new { ok = false });", route);
    }

    [Fact]
    public void I02_SyncData_ReadsTheStatusGuarded()
    {
        var page = Body(Source("Browser", "SettingsPage.cs"), "internal static object SyncData(bool isPrivate)", "\n    }\n");
        Assert.Contains("Sync.SyncService.Guarded(() => Sync.SyncService.Status, null)", page);
        Assert.DoesNotContain("StatusData(Sync.SyncService.Status,", page);
    }
}
