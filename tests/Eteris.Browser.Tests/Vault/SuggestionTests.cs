using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// C3 (docs/palavras-passe.md): a lista de sugestões por cima da página — onde fica (zoom, escala do ecrã, ecrã
// dividido), que teclas apanha (só com a lista aberta), o que sugere e quando se pode preencher.
public class SuggestionGeometryTests
{
    // A página de uma aba sozinha: abaixo das barras (80 DIP), 1000×700.
    private static readonly Rect Page = new(0, 80, 1000, 700);

    [Fact]
    public void BelowTheField_AtTheSameLeft_AsWideAsTheField()
    {
        var place = SuggestionGeometry.Place(Page, new VaultRect(100, 200, 300, 32), zoom: 1, listHeight: 120);
        Assert.NotNull(place);
        Assert.Equal(100, place.X);
        Assert.Equal(80 + 200 + 32 + SuggestionGeometry.Gap, place.Y);
        Assert.Equal(300, place.Width);
        Assert.False(place.Above);
    }

    [Fact]
    public void NarrowField_GetsTheMinimumWidth()
        => Assert.Equal(SuggestionGeometry.MinWidth, SuggestionGeometry.Place(Page, new VaultRect(100, 200, 120, 32), 1, 120)!.Width);

    // Zoom da página a 150%: os píxeis CSS do retângulo valem 1,5 DIP.
    [Fact]
    public void PageZoom_ScalesTheCssRectangle()
    {
        var place = SuggestionGeometry.Place(Page, new VaultRect(100, 200, 300, 32), zoom: 1.5, listHeight: 120)!;
        Assert.Equal(150, place.X);
        Assert.Equal(80 + (200 + 32) * 1.5 + SuggestionGeometry.Gap, place.Y);
        Assert.Equal(450, place.Width);
    }

    // A escala do ecrã não entra: em DIP, píxeis CSS × zoom (o Chromium desenha CSS × zoom × escala em píxeis físicos e
    // o WPF divide pela mesma escala). A mesma página a 100% e a 150% do Windows dá o mesmo sítio.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DisplayScale_DoesNotMoveTheList(double dpi)
    {
        // A área da página medida em píxeis físicos e convertida para DIP pela escala do ecrã.
        var physical = new Rect(0 * dpi, 80 * dpi, 1000 * dpi, 700 * dpi);
        var page = new Rect(physical.X / dpi, physical.Y / dpi, physical.Width / dpi, physical.Height / dpi);
        var place = SuggestionGeometry.Place(page, new VaultRect(100, 200, 300, 32), 1.25, 120)!;
        Assert.Equal(125, place.X, 6);
        Assert.Equal(80 + 232 * 1.25 + SuggestionGeometry.Gap, place.Y, 6);
    }

    // Ecrã dividido: a página da direita começa a meio da janela.
    [Fact]
    public void SplitScreen_UsesThePaneOrigin()
    {
        var right = new Rect(504, 80, 496, 700);
        var place = SuggestionGeometry.Place(right, new VaultRect(20, 100, 200, 30), 1, 100)!;
        Assert.Equal(524, place.X);
        Assert.Equal(80 + 130 + SuggestionGeometry.Gap, place.Y);
        // Nunca sai da metade dela para a outra.
        var nearEdge = SuggestionGeometry.Place(right, new VaultRect(400, 100, 90, 30), 1, 100)!;
        Assert.Equal(right.Right - nearEdge.Width, nearEdge.X);
        Assert.True(nearEdge.X >= right.Left);
    }

    [Fact]
    public void NoRoomBelow_GoesAbove()
    {
        var place = SuggestionGeometry.Place(Page, new VaultRect(100, 620, 300, 32), 1, 160)!;
        Assert.True(place.Above);
        Assert.Equal(80 + 620 - SuggestionGeometry.Gap - 160, place.Y);
    }

    [Fact]
    public void NoRoomEitherWay_StaysBelowAndIsClipped()
    {
        var place = SuggestionGeometry.Place(new Rect(0, 80, 1000, 200), new VaultRect(10, 60, 300, 32), 1, 400)!;
        Assert.False(place.Above);
    }

    [Theory]
    [InlineData(100, 720, 300, 32)]
    [InlineData(100, -50, 300, 32)]
    [InlineData(1200, 200, 300, 32)]
    [InlineData(-400, 200, 300, 32)]
    public void FieldOutOfThePage_NoList(double x, double y, double w, double h)
        => Assert.Null(SuggestionGeometry.Place(Page, new VaultRect(x, y, w, h), 1, 120));

    [Fact]
    public void NarrowPage_ListNeverWiderThanThePage()
    {
        var place = SuggestionGeometry.Place(new Rect(0, 80, 200, 700), new VaultRect(10, 100, 150, 30), 1, 100)!;
        Assert.Equal(200, place.Width);
        Assert.Equal(0, place.X);
    }
}

public class VaultKeysTests
{
    private static VaultKeyAction K(Key key, bool open, bool selection = false, bool canOpen = false, bool ctrl = false, bool shift = false, bool alt = false)
        => VaultKeys.FromPage(key, ctrl, shift, alt, open, selection, canOpen);

    // Com a lista fechada, nada é do cofre (a página recebe tudo) — exceto ↓ num campo com sugestões, que a abre.
    [Theory]
    [InlineData(Key.Down)]
    [InlineData(Key.Up)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    [InlineData(Key.A)]
    public void ListClosed_NothingIsTaken(Key key) => Assert.Equal(VaultKeyAction.None, K(key, open: false));

    [Fact]
    public void ListClosed_DownOpensIt_OnlyInAFieldWithSuggestions()
    {
        Assert.Equal(VaultKeyAction.Open, K(Key.Down, open: false, canOpen: true));
        Assert.True(VaultKeys.Consumes(VaultKeyAction.Open));
        Assert.Equal(VaultKeyAction.None, K(Key.Up, open: false, canOpen: true));
        Assert.Equal(VaultKeyAction.None, K(Key.Down, open: false, canOpen: true, alt: true));
    }

    [Fact]
    public void ListOpen_ArrowsMoveAndAreTaken()
    {
        Assert.Equal(VaultKeyAction.Previous, K(Key.Up, open: true));
        Assert.Equal(VaultKeyAction.Next, K(Key.Down, open: true));
        Assert.True(VaultKeys.Consumes(VaultKeyAction.Previous));
        Assert.True(VaultKeys.Consumes(VaultKeyAction.Next));
    }

    // Enter só é do cofre com uma linha escolhida; sem escolha, fecha a lista e segue para a página (enviar o formulário).
    [Fact]
    public void ListOpen_Enter()
    {
        Assert.Equal(VaultKeyAction.Accept, K(Key.Enter, open: true, selection: true));
        Assert.True(VaultKeys.Consumes(VaultKeyAction.Accept));
        Assert.Equal(VaultKeyAction.CloseAndPass, K(Key.Enter, open: true, selection: false));
        Assert.False(VaultKeys.Consumes(VaultKeyAction.CloseAndPass));
    }

    [Fact]
    public void ListOpen_EscapeClosesAndIsTaken_TabClosesAndGoesOn()
    {
        Assert.Equal(VaultKeyAction.Close, K(Key.Escape, open: true));
        Assert.True(VaultKeys.Consumes(VaultKeyAction.Close));
        Assert.Equal(VaultKeyAction.CloseAndPass, K(Key.Tab, open: true, selection: true));
        Assert.Equal(VaultKeyAction.CloseAndPass, K(Key.Tab, open: true, shift: true));
    }

    // Escrever continua a ir para a página (filtra a lista); os atalhos com Ctrl ou Alt não são da lista.
    [Theory]
    [InlineData(Key.A, false, false)]
    [InlineData(Key.Back, false, false)]
    [InlineData(Key.Left, false, false)]
    [InlineData(Key.Home, false, false)]
    [InlineData(Key.Down, true, false)]
    [InlineData(Key.Enter, false, true)]
    public void ListOpen_OtherKeysGoToThePage(Key key, bool ctrl, bool alt)
        => Assert.Equal(VaultKeyAction.None, K(key, open: true, selection: true, ctrl: ctrl, alt: alt));
}

public class VaultFillPolicyTests
{
    private static LoginEntry L(string id, string origin, string user, OriginMatch match) => new(id, origin, user, match, new JsonObject());

    [Fact]
    public void Suggestions_ExactFirst_ThenSameSiteMarked()
    {
        var rows = VaultFillPolicy.Suggestions(
        [
            L("b", "https://conta.exemplo.com", "ana@conta", OriginMatch.SameSite),
            L("a", "https://exemplo.com", "ana", OriginMatch.Exact),
        ], "https://exemplo.com", prefix: null);
        Assert.Equal(["a", "b"], rows.Select(r => r.Id));
        Assert.Equal("ana", rows[0].Title);
        Assert.Equal("exemplo.com", rows[0].Detail);
        Assert.Equal("de conta.exemplo.com", rows[1].Detail);
        Assert.False(rows[0].Insecure);
    }

    // Uma credencial de https nunca aparece em http; as de http aparecem com o aviso (exceto no próprio computador).
    [Fact]
    public void Http_NeverGetsHttpsCredentials_AndWarns()
    {
        var page = "http://exemplo.test";
        var rows = VaultFillPolicy.Suggestions(
        [
            L("s", "https://exemplo.test", "seguro", OriginMatch.Exact),
            L("h", "http://exemplo.test", "inseguro", OriginMatch.Exact),
        ], page, null);
        Assert.Equal(["h"], rows.Select(r => r.Id));
        Assert.True(rows[0].Insecure);
        Assert.Equal("Ligação não segura", rows[0].Detail);
        var local = VaultFillPolicy.Suggestions([L("l", "http://127.0.0.1:8899", "ana", OriginMatch.Exact)], "http://127.0.0.1:8899", null);
        Assert.False(local[0].Insecure);
    }

    // Domínios parecidos não têm sugestões (a correspondência é exata ou do mesmo domínio registável).
    [Fact]
    public void LookAlikeDomains_GetNothing()
        => Assert.Empty(VaultFillPolicy.Suggestions([L("g", "https://github.com", "ana", OriginMatch.Exact)], "https://g1thub.com", null));

    [Fact]
    public void Prefix_FiltersByUsername()
    {
        LoginEntry[] logins = [L("a", "https://x.test", "Ana.Teste", OriginMatch.Exact), L("b", "https://x.test", "bruno", OriginMatch.Exact),
            L("c", "https://x.test", "", OriginMatch.Exact)];
        Assert.Equal(["a"], VaultFillPolicy.Suggestions(logins, "https://x.test", "ana").Select(r => r.Id));
        Assert.Equal(3, VaultFillPolicy.Suggestions(logins, "https://x.test", "").Count);
        Assert.Equal("(sem utilizador)", VaultFillPolicy.Suggestions(logins, "https://x.test", null)[2].Title);
    }

    // Onde aparecem as sugestões de palavras-passe: no utilizador e na palavra-passe atual (não na nova nem num e-mail
    // de uma newsletter).
    [Theory]
    [InlineData("username", "login", true)]
    [InlineData("username", "other", true)]
    [InlineData("password", "login", true)]
    [InlineData("password", "change", true)]
    [InlineData("new-password", "signup", false)]
    [InlineData("new-password", "change", false)]
    [InlineData("email", "other", false)]
    [InlineData("one-time-code", "other", false)]
    [InlineData("cc-number", "payment", false)]
    public void WhereLoginsAreSuggested(string kind, string formKind, bool offered)
        => Assert.Equal(offered, VaultFillPolicy.OffersLogins(kind, formKind));

    [Fact]
    public void Fill_OnlyOnTheVisibleTabOfTheActiveWindow_InTheSameDocument()
    {
        var ok = new VaultFillPolicy.FillCheck(TabVisible: true, WindowActive: true, SameDocument: true, SavedOrigin: "https://exemplo.com", PageOrigin: "https://exemplo.com");
        Assert.Equal(FillVerdict.Allowed, VaultFillPolicy.CanFill(ok));
        Assert.Equal(FillVerdict.TabHidden, VaultFillPolicy.CanFill(ok with { TabVisible = false }));
        Assert.Equal(FillVerdict.WindowInactive, VaultFillPolicy.CanFill(ok with { WindowActive = false }));
        Assert.Equal(FillVerdict.DocumentGone, VaultFillPolicy.CanFill(ok with { SameDocument = false }));
        Assert.Equal(FillVerdict.Allowed, VaultFillPolicy.CanFill(ok with { SavedOrigin = "https://conta.exemplo.com" }));
        Assert.Equal(FillVerdict.OtherSite, VaultFillPolicy.CanFill(ok with { PageOrigin = "https://g1thub.com" }));
        Assert.Equal(FillVerdict.OtherSite, VaultFillPolicy.CanFill(ok with { PageOrigin = "http://exemplo.com" }));
    }

    // Escolher à mão noutro domínio pede confirmação com as duas origens completas (esquema e porta) (C6, menu de
    // contexto); numa página http, com um aviso.
    [Fact]
    public void OtherSite_ConfirmationNamesBothOrigins()
    {
        Assert.Equal("Esta palavra-passe é de https://github.com. Usar em https://g1thub.com?", VaultFillPolicy.OtherSiteQuestion("https://github.com", "https://g1thub.com"));
        var http = VaultFillPolicy.OtherSiteQuestion("http://velho.exemplo", "http://outro.exemplo:8080");
        Assert.StartsWith("Esta palavra-passe é de http://velho.exemplo. Usar em http://outro.exemplo:8080?", http);
        Assert.Contains("não é segura", http);
    }

    // Revisão: uma palavra-passe de https nunca vai para uma página http, nem escolhida à mão (desenho, secção 3.6).
    [Theory]
    [InlineData("https://banco.exemplo", "http://banco.exemplo", false)]
    [InlineData("https://banco.exemplo", "https://outro.exemplo", true)]
    [InlineData("http://velho.exemplo", "http://outro.exemplo", true)]
    [InlineData("https://banco.exemplo", "http://localhost:3000", true)]
    [InlineData("android://x@com.exemplo/", "https://outro.exemplo", false)]
    public void OtherSite_NeverFromHttpsToHttp(string saved, string page, bool offered)
        => Assert.Equal(offered, VaultFillPolicy.OffersElsewhere(saved, page));

    // Contra cliques roubados: um clique nos primeiros 500 ms da lista não conta.
    [Fact]
    public void Clicks_InTheFirst500ms_AreIgnored()
    {
        Assert.False(VaultFillPolicy.AcceptsClick(TimeSpan.FromMilliseconds(120)));
        Assert.False(VaultFillPolicy.AcceptsClick(TimeSpan.FromMilliseconds(499)));
        Assert.True(VaultFillPolicy.AcceptsClick(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Click_WithoutPointerLeavingTheRowAfterShow_IsIgnored()
    {
        Assert.False(VaultFillPolicy.AcceptsPointer(rowUnderCursorAtShow: true, cursorLeftSince: false));
        Assert.True(VaultFillPolicy.AcceptsPointer(rowUnderCursorAtShow: true, cursorLeftSince: true));
        Assert.True(VaultFillPolicy.AcceptsPointer(rowUnderCursorAtShow: false, cursorLeftSince: false));
        Assert.False(VaultFillPolicy.CountsAsLeaving(3.9));   // a mão treme ao clicar; a lista a fugir de um cursor parado dá 0
        Assert.True(VaultFillPolicy.CountsAsLeaving(4));
    }

    [Fact]
    public void Enter_InTheFirst500msOfTheList_DoesNotFill()
    {
        Assert.False(VaultFillPolicy.AcceptsKey(TimeSpan.FromMilliseconds(499)));
        Assert.True(VaultFillPolicy.AcceptsKey(TimeSpan.FromMilliseconds(500)));
        var window = File.ReadAllText(Path.Combine(Eteris.Browser.Tests.Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.Vault.cs"));
        Assert.Contains("VaultFillPolicy.AcceptsKey(DateTime.UtcNow - _vaultShownAt)", window);
        var list = File.ReadAllText(Path.Combine(Eteris.Browser.Tests.Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Vault", "SuggestionList.xaml.cs"));
        Assert.Contains("VaultFillPolicy.AcceptsPointer(", list);
    }

    // D-5: preencher palavras-passe só pede o Windows Hello com o interruptor ligado.
    [Fact]
    public void Reauth_ForPasswords_FollowsTheSetting()
    {
        Assert.False(VaultFillPolicy.NeedsReauth(askHello: false));
        Assert.True(VaultFillPolicy.NeedsReauth(askHello: true));
    }
}
