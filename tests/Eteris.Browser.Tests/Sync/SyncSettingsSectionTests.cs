using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Sync;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Tests.Sync;

// Definições › Sincronização mais curta (0.2.15, T14): com sessão, o estado numa linha à frente; sem sessão, título,
// uma frase, um botão e uma ligação; o resto recolhido em «Mais opções», sem nunca esconder o que pede atenção.
public class SyncSettingsSectionTests
{
    private const long Now = 1_762_560_000_000;
    private const long Minute = 60_000;

    // ---- a linha de estado (SyncService.Headline) --------------------------------------------------------------------

    [Fact]
    public void Headline_SaysConnectedAndWhenItLastSynced()
    {
        Assert.Equal("Ligada · última sincronização há 2 min", SyncService.Headline(TestStatus.Of(SyncPhase.On, last: Now - 2 * Minute), Now));
        Assert.Equal("Ligada · última sincronização agora mesmo", SyncService.Headline(TestStatus.Of(SyncPhase.On, last: Now - 10_000), Now));
        Assert.Equal("Ligada · última sincronização há 3 h", SyncService.Headline(TestStatus.Of(SyncPhase.On, last: Now - 180 * Minute), Now));
    }

    // Um computador à espera de aprovação, a escolher, parado ou desligado não está «Ligada»: fala o item próprio.
    [Fact]
    public void Headline_IsNull_WhenThereIsSomethingElseToSay()
    {
        var last = Now - 2 * Minute;
        Assert.Null(SyncService.Headline(null, Now));
        foreach (var phase in new[] { SyncPhase.Off, SyncPhase.Choose, SyncPhase.Pairing, SyncPhase.Stopped })
            Assert.Null(SyncService.Headline(TestStatus.Of(phase, last: last), Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last) with { NeedsSignIn = true }, Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last) with { Deleting = true }, Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last, error: "x"), Now));
        Assert.Null(SyncService.Headline(TestStatus.Of(SyncPhase.On, last: last) with { NeedsUpdate = true }, Now));
    }

    [Fact]
    public void Headline_WhileStarting_WhileRunning_AndBeforeTheFirstSync()
    {
        Assert.Equal("A ligar…", SyncService.Headline(TestStatus.Of(SyncPhase.Starting), Now));
        Assert.Equal("A sincronizar…", SyncService.Headline(TestStatus.Of(SyncPhase.On, running: true, last: Now - 2 * Minute), Now));
        Assert.Equal("Ligada · a primeira sincronização está a caminho", SyncService.Headline(TestStatus.Of(SyncPhase.On), Now));
    }

    [Fact]
    public void StatusData_CarriesTheHeadline_AndKeepsTheOldFields()
    {
        var data = SyncService.StatusData(TestStatus.Of(SyncPhase.On, last: Now - 2 * Minute), Now).ToString()!;
        Assert.Contains("headline = Ligada · última sincronização há 2 min", data, StringComparison.Ordinal);
        Assert.Contains("lastText = ", data, StringComparison.Ordinal);
        Assert.Contains("summary = ", data, StringComparison.Ordinal);
    }

    // ---- a página (settings.html) ------------------------------------------------------------------------------------

    private const string Lead = "Os seus favoritos, palavras-passe e definições em todos os seus computadores, cifrados ponta a ponta.";
    private const string MoreTag = "<details class=\"syncmore\" id=\"syncMore\"";

    private static string Page() => File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "settings.html"));

    // O JS da página: a partir do <script> sem tipo (o primeiro <script> é o dos dados, em JSON).
    private static string Script(string page) => page[At(page, "<script>")..];

    private static string Section(string page)
    {
        var start = At(page, "<section id=\"sincronizacao\">");
        return page[start..page.IndexOf("</section>", start, StringComparison.Ordinal)];
    }

    private static int At(string text, string what)
    {
        var at = text.IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, "falta: " + what);
        return at;
    }

    private static int Count(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // Onde começa e acaba o bloco «Mais opções» (não há <details> dentro dele).
    private static (int Start, int End) More(string page)
    {
        var start = At(page, MoreTag);
        var end = page.IndexOf("</details>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return (start, end);
    }

    private static bool InsideMore(string page, string id)
    {
        var (start, end) = More(page);
        var at = At(page, $"id=\"{id}\"");
        return at > start && at < end;
    }

    [Fact]
    public void SignedOut_ShowsTitleSentenceButtonAndLink()
    {
        var section = Section(Page());
        Assert.Contains("<h2>Sincronização</h2>", section, StringComparison.Ordinal);
        Assert.Contains(Lead, section, StringComparison.Ordinal);
        Assert.Contains("id=\"googleButton\" aria-describedby=\"googleNote\">Entrar com o Google</button>", section, StringComparison.Ordinal);
        Assert.Contains("id=\"syncHow\" href=\"eteris://privacy/#sincronizacao\">Como protegemos os seus dados</a>", section, StringComparison.Ordinal);
        Assert.Contains("<b id=\"googleTitle\" hidden></b>", section, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedOut_TheLongParagraphIsGone()
    {
        var page = Page();
        Assert.DoesNotContain("com uma segunda cifra própria — as palavras-passe", page, StringComparison.Ordinal);
        // O que se lê sem sessão no item da conta: da frase até ao botão, sem as etiquetas nem o que nasce escondido.
        var lead = At(page, "id=\"syncLead\"");
        var visible = page[page.LastIndexOf('<', lead)..At(page, "id=\"googleButton\"")];
        visible = Regex.Replace(visible, @"<!--.*?-->", "", RegexOptions.Singleline);
        visible = Regex.Replace(visible, @"<(\w+)\b[^>]*\shidden\b[^>]*>.*?</\1>", "", RegexOptions.Singleline);
        visible = Regex.Replace(visible, @"<[^>]*>?", " ");
        visible = Regex.Replace(visible, @"\s+", " ").Trim();
        Assert.Contains(Lead, visible, StringComparison.Ordinal);
        Assert.Contains("Como protegemos os seus dados", visible, StringComparison.Ordinal);
        Assert.True(visible.Length < 200, visible);
    }

    [Fact]
    public void SignedIn_HeadlineComesFirst()
    {
        var page = Page();
        var headline = At(page, "id=\"syncHeadline\"");
        Assert.True(headline > At(page, "<h2>Sincronização</h2>"));
        Assert.True(headline < At(page, "id=\"googleButton\""));
        Assert.True(headline < At(page, "id=\"syncBody\""));
        Assert.True(headline < At(page, "id=\"syncMore\""));
        Assert.Contains("<p class=\"synchead\" id=\"syncHeadline\" role=\"status\" hidden></p>", page, StringComparison.Ordinal);
        Assert.Contains("s.headline", Script(page), StringComparison.Ordinal);
    }

    [Fact]
    public void SyncNow_LivesOnlyInsideMoreOptions()
    {
        var page = Page();
        Assert.Equal(1, Count(page, "id=\"syncNow\""));
        Assert.True(InsideMore(page, "syncNow"));
        var (start, end) = More(page);
        Assert.Contains("<summary>Mais opções</summary>", page[start..end], StringComparison.Ordinal);
        // Fechado por omissão.
        var tag = page[start..page.IndexOf('>', start)];
        Assert.DoesNotContain(" open", tag, StringComparison.Ordinal);
        // Dentro de syncBody (só existe à vista com sessão), antes de «Exportar perfil cifrado».
        Assert.True(start > At(page, "id=\"syncBody\""));
        Assert.True(end < At(page, "Exportar perfil cifrado"));
    }

    // R5: o que pede uma ação à pessoa, ou a avisa de um risco, nunca fica escondido em «Mais opções».
    [Fact]
    public void WhatNeedsAttention_StaysOutsideMoreOptions()
    {
        var page = Page();
        foreach (var id in new[]
        {
            "syncNotice", "syncReauth", "syncState", "syncOffItem", "syncChooseItem", "syncPairItem", "syncReplacedItem",
            "syncRequests", "syncRequestsNote", "syncKitItem", "syncOfferItem", "syncExitNote", "syncPrivateNote", "googleNote",
            "googleUrl", "syncHow", "syncAccountItem",
        })
            Assert.False(InsideMore(page, id), id);
        // O aviso de pedidos em série (T8) é desenhado pelo JS dentro de syncRequests (que está fora): não é HTML estático.
        Assert.Contains("revoke.id = 'syncQuietRevoke'", page, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreOptions_HoldsDevicesTopicsWhatSyncsSignOutAndDelete()
    {
        var page = Page();
        foreach (var id in new[]
        {
            "syncNow", "syncNameItem", "syncDevicesItem", "syncLost", "syncTopicItem", "syncWhat", "syncStays", "syncWhen",
            "syncSignOut", "syncOutHelp", "syncForgetRow", "syncForgetNote", "syncRevokeItem", "syncDeleteItem",
        })
            Assert.True(InsideMore(page, id), id);
    }

    // A sondagem de 1,5 s não pode fechar «Mais opções» na cara da pessoa: o JS só o abre, num único sítio.
    [Fact]
    public void Paint_NeverRebuildsOrClosesMoreOptions()
    {
        var script = Script(Page());
        Assert.DoesNotContain("syncMore.innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".open = false", script, StringComparison.Ordinal);
        Assert.DoesNotContain("removeAttribute('open')", script, StringComparison.Ordinal);
        Assert.Equal(1, Count(script, ".open = true"));
        var definition = At(script, "const openMoreFor = el =>");
        var line = script[definition..script.IndexOf('\n', definition)];
        Assert.Contains(".open = true", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_KeepsEveryIdTheScriptUses()
    {
        var page = Page();
        foreach (var id in new[]
        {
            "googleTitle", "googleNote", "googleUrl", "googleButton", "syncPrivateNote", "syncBody", "syncState", "syncNotice",
            "syncReauth", "syncNow", "syncRequests", "syncDevicesItem", "syncTopicItem", "syncWhat", "syncStays", "syncWhen",
            "syncDeleteItem", "syncKitItem", "syncForgetRow", "syncForgetNote",
            "syncHeadline", "syncAccountItem", "syncLead", "syncHow", "syncMore", "syncSignOut",
        })
            Assert.True(Count(page, $"id=\"{id}\"") == 1, id);
        Assert.DoesNotContain("id=\"syncCodeItem\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"syncNewCodeItem\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateWindow_StillSeesNothingOfTheAccount()
    {
        Assert.Contains("\"syncPrivate\":true", SettingsPage.Render(new UserSettings(), isPrivate: true), StringComparison.Ordinal);
        Assert.False(InsideMore(Page(), "syncPrivateNote"));
    }

    [Fact]
    public void Page_HasNoNewNetworkPower()
    {
        var page = Page();
        Assert.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-src eteris:\">", page, StringComparison.Ordinal);
        Assert.Equal(1, Count(page, "Content-Security-Policy"));
        foreach (var text in new[] { Section(page), page })
        {
            Assert.DoesNotContain("fetch(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("XMLHttpRequest", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MoreOptions_IsThemedInLightAndDark()
    {
        var page = Page();
        var style = page[At(page, "<style>")..At(page, "</style>")];
        Assert.Matches(new Regex(@"(^|\n)\s*\.syncmore\b[^{\n]*\{"), style);
        Assert.Matches(new Regex(@"html\.light \.syncmore\b[^{\n]*\{"), style);
        Assert.Matches(new Regex(@"(^|\n)\s*\.synchead\b[^{\n]*\{"), style);
        Assert.Matches(new Regex(@"html\.light \.synchead\b[^{\n]*\{"), style);
        // O <summary> recebe o foco do teclado e mostra o anel.
        Assert.Contains(".syncmore > summary:focus-visible", style, StringComparison.Ordinal);
    }

    // R2 (S33): quem ainda não entrou lê o que vai para a Google na página de privacidade interna (a outra metade, o bloco
    // #sincronizacao em eteris://privacy/, é da T15).
    [Fact]
    public void SignedOut_CanReadWhatGoesToGoogle_BeforeSigningIn()
    {
        var page = Page();
        Assert.Contains("id=\"syncHow\" href=\"eteris://privacy/#sincronizacao\"", page, StringComparison.Ordinal);
        Assert.True(At(page, "id=\"syncHow\"") < At(page, "id=\"syncBody\""));
        Assert.False(InsideMore(page, "syncHow"));
        // Nada a esconde, com ou sem sessão, nem em janela privada.
        Assert.DoesNotContain("syncHow", Script(page), StringComparison.Ordinal);
        Assert.Contains("id=\"syncHow\" href=\"eteris://privacy/#sincronizacao\"", SettingsPage.Render(new UserSettings(), isPrivate: true), StringComparison.Ordinal);
    }

    // R10: «Sair deste computador» num botão próprio, em «Mais opções»; o googleButton só entra, cancela ou liga.
    [Fact]
    public void SignOut_LivesInMoreOptions_WithItsHelpAndTheForgetRow()
    {
        var page = Page();
        Assert.Matches(new Regex("id=\"syncSignOut\"[^>]*>Sair deste computador</button>"), page);
        foreach (var id in new[] { "syncSignOut", "syncOutHelp", "syncForgetRow", "syncForgetNote" })
            Assert.True(InsideMore(page, id), id);
        var script = Script(page);
        foreach (var text in new[]
        {
            "googleButton.hidden = g.state === 'signedIn';",
            "syncSignOut.hidden = g.state !== 'signedIn';",
            "googleButton.textContent = g.state === 'waiting' ? 'Cancelar' : g.folder ? 'Ligar' : 'Entrar com o Google';",
            "googleButton.dataset.state = g.state;",
            "syncSignOut.addEventListener('click',",
            "lastState.sync.kit.state !== 'saved'",
        })
            Assert.Contains(text, script, StringComparison.Ordinal);
        Assert.Equal(1, Count(page, "'sync-google-out'"));
        Assert.DoesNotContain("? 'Sair' :", page, StringComparison.Ordinal);
        // A rota de sair só se pede no clique do syncSignOut.
        var click = At(script, "syncSignOut.addEventListener('click',");
        Assert.True(At(script, "'sync-google-out'") > click);
        var style = page[At(page, "<style>")..At(page, "</style>")];
        Assert.Matches(new Regex(@"[^{}]*#googleButton\[hidden\][^{}]*\{\s*display:\s*none;\s*\}"), style);
        Assert.Matches(new Regex(@"[^{}]*#syncSignOut\[hidden\][^{}]*\{\s*display:\s*none;\s*\}"), style);
        Assert.Matches(new Regex(@"[^{}]*#googleTitle\[hidden\][^{}]*\{\s*display:\s*none;\s*\}"), style);
    }

    // R11 (a, b): é isto que faz eteris://settings/#syncRevokeItem (barras da T8 e da T7) e eteris://settings/#syncTopicItem
    // (barra «Sincronizado.» da T10) chegarem ao destino com «Mais opções» fechado.
    [Fact]
    public void Anchors_OpenMoreOptions()
    {
        var page = Page();
        var script = Script(page);
        Assert.Contains("const openMoreFor = el =>", script, StringComparison.Ordinal);
        Assert.Equal(1, Count(script, "addEventListener('hashchange'"));
        Assert.Contains("'#sincronizacao-mais'", script, StringComparison.Ordinal);
        // Carregamento e hashchange (o mesmo tratador: #syncRevokeItem, #sincronizacao-mais e qualquer outro destino) e
        // a pesquisa. A ligação syncLostLink já não existe (a T12c retirou-a): não há um quarto sítio.
        Assert.True(Count(script, "openMoreFor(") >= 3);
        Assert.DoesNotContain("syncLostLink", page, StringComparison.Ordinal);
        Assert.Contains("addEventListener('hashchange', goToAnchor);", script, StringComparison.Ordinal);
        // Ao carregar: logo depois da primeira pintura.
        var load = At(script, "paint(initial);");
        Assert.True(script.IndexOf("goToAnchor();", load, StringComparison.Ordinal) > load);
        Assert.Contains("'#syncRevokeItem'", script, StringComparison.Ordinal);
        Assert.Contains("$('syncRevoke').focus();", script, StringComparison.Ordinal);
        // Em cada tratador, abre primeiro e só depois leva a vista.
        foreach (var (from, to) in new[] { ("const focusRevoke = () => {", "const toAnchor"), ("const goToAnchor = () => {", "addEventListener('hashchange'") })
        {
            var start = At(script, from);
            var body = script[start..script.IndexOf(to, start, StringComparison.Ordinal)];
            Assert.True(At(body, "openMoreFor(") < At(body, "scrollIntoView({ block: 'center' })"), from);
        }
    }

    // R12: com sessão o googleButton esconde-se; a secção mede-se no item da conta, que nunca se esconde.
    [Fact]
    public void SectionInView_IsMeasuredOnAnElementThatIsAlwaysVisible()
    {
        var page = Page();
        var script = Script(page);
        Assert.Contains(".observe($('syncAccountItem'))", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".observe(googleButton)", script, StringComparison.Ordinal);
        Assert.False(InsideMore(page, "syncAccountItem"));
        Assert.DoesNotMatch(new Regex("id=\"syncAccountItem\"[^>]*hidden"), page);
        var isSignOut = At(script, "const isSignOut = e =>");
        Assert.Contains("syncSignOut.contains(e.target)", script[isSignOut..script.IndexOf('\n', isSignOut)], StringComparison.Ordinal);
        Assert.Contains("if (!isSignOut(e)) { askAccount(); askLook(); }", script, StringComparison.Ordinal);
    }

    // R3/R5: um apagar do Drive (a correr ou parado com erro) fala no próprio item, que vive em «Mais opções»: a pintura
    // abre o bloco uma vez quando o apagar começa (a nota com o porquê e «Tentar de novo» ficam à vista), sem o reabrir
    // em cada sondagem se a pessoa o fechar.
    [Fact]
    public void Deleting_OpensMoreOptions_OncePerDelete()
    {
        var script = Script(Page());
        var start = At(script, "const paintSync = d => {");
        var paintSync = script[start..At(script, "const paint = d => {")];
        Assert.Contains("if (s.deleting && !deleteOpened) { deleteOpened = true; openMoreFor($('syncDeleteItem')); }", paintSync, StringComparison.Ordinal);
        Assert.Contains("if (!s.deleting) deleteOpened = false;", paintSync, StringComparison.Ordinal);
        Assert.True(At(script, "let deleteOpened = false;") < start);
    }

    // R11 (c): a pesquisa das Definições encontra o que está dentro de «Mais opções» (e abre-o).
    [Fact]
    public void Search_FindsWhatIsInsideMoreOptions()
    {
        var page = Page();
        var search = page[At(page, "// ---------- Procurar nas definições ----------")..];
        Assert.Contains("syncMore", search, StringComparison.Ordinal);
        Assert.Contains("openMoreFor(", search, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"[^{}\n]*details\.filtered-out[^{}\n]*\{\s*display:\s*none !important;"), page);
    }
}
