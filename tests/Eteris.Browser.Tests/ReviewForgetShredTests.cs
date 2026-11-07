using System.IO;
using System.Text.Json;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Revisão aos 50: «Triturar», «Esquecer ao fechar», a janela privada e o cadeado — o que estava errado não volta.
public sealed class ReviewForgetShredTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-rev50-" + Guid.NewGuid() + ".db");
    private static readonly DateTime T0 = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, "src", "Eteris.Browser", .. parts]));

    // Fechar a janela, apagar um Espaço ou ir para outro site: só os sites com «esquecer» que ficam sem nenhuma aba.
    [Fact]
    public void SitesLeaving_OnlyForgetSitesWithNoTabLeft()
    {
        static bool Forget(string site) => site is "banco.pt" or "rede.pt" or "alice.github.io";
        var leaving = SiteScope.SitesLeaving(
            ["https://www.banco.pt/conta", "https://rede.pt/", "https://jornal.pt/", "https://alice.github.io/", "eteris://newtab/", null],
            ["https://loja.rede.pt/", "https://bob.github.io/"], Forget);
        Assert.Equal(["banco.pt", "alice.github.io"], leaving);
        Assert.Empty(SiteScope.SitesLeaving(["https://banco.pt/"], ["https://banco.pt/outra"], Forget));
    }

    [Fact]
    public void ShredSummary_NamesLockedAndNotedTabs()
    {
        var text = SiteScope.ShredSummary("exemplo.pt", 3, 2, closingTabs: 3, locked: 1, noted: 1);
        Assert.Contains("As 3 abas deste site nesta janela fecham, incluindo 1 trancada e 1 com nota", text);
        Assert.Contains("A aba deste site nesta janela fecha.", SiteScope.ShredSummary("exemplo.pt", 0, 0, closingTabs: 1));
        Assert.DoesNotContain("abas deste site", SiteScope.ShredSummary("exemplo.pt", 0, 0, closingTabs: 0));
    }

    // Na janela privada não há histórico, e as permissões são as dadas nessa janela.
    [Fact]
    public void ShredSummary_InPrivate_PromisesNoHistory()
    {
        var text = SiteScope.ShredSummary("exemplo.pt", 2, 0, isPrivate: true, closingTabs: 1);
        Assert.DoesNotContain("histórico", text);
        Assert.Contains("as permissões dadas nesta janela", text);
        Assert.Contains("2 cookies", text);
        Assert.StartsWith("Vai apagar de localhost:", SiteScope.ShredSummary("localhost", 1, 1));
    }

    // O aviso não anuncia o armazenamento apagado quando não havia motor para o apagar.
    [Fact]
    public void ShredDone_SaysWhenStorageWasLeft()
    {
        Assert.Equal("3 cookies e 1 entrada do histórico apagados", SiteScope.ShredDone(3, 1, storage: true));
        Assert.Equal("1 cookie apagado", SiteScope.ShredDone(1, 0, storage: true, isPrivate: true));
        Assert.Contains("o armazenamento ficou por apagar", SiteScope.ShredDone(0, 0, storage: false));
    }

    // Histórico do site numa só passagem: conta, endereços e apagar; o resto fica.
    [Fact]
    public void History_TakeSite_DeletesCountsAndReturnsTheUrls()
    {
        var store = new HistoryStore(_path);
        store.Record("https://www.exemplo.pt/a", "A", T0);
        store.Record("https://www.exemplo.pt/a", "A", T0.AddMinutes(1));
        store.Record("https://loja.exemplo.pt/b", "B", T0.AddMinutes(2));
        store.Record("https://malexemplo.pt/c", "C", T0.AddMinutes(3));
        store.Record("https://outro.pt/?ref=exemplo.pt", "D", T0.AddMinutes(4));
        var (count, urls) = store.TakeSite("exemplo.pt");
        Assert.Equal(3, count);
        Assert.Equal(["https://loja.exemplo.pt/b", "https://www.exemplo.pt/a"], urls.Order());
        Assert.Equal(0, store.CountSite("exemplo.pt"));
        Assert.Equal(2, store.Visits(10).Count);
    }

    // Triturar google.com leva a câmara de meet.google.com; as chaves com porta também; os vizinhos ficam.
    [Fact]
    public void Permissions_ForgetWhere_CoversSubdomainsAndPorts()
    {
        var store = new SitePermissionStore(_path);
        store.Set("meet.google.com", SitePermission.Camera, true);
        store.Set("google.com", SitePermission.Location, false);
        store.Set("app.exemplo.pt:8443", SitePermission.LocalNetwork, true);
        store.Set("notgoogle.com", SitePermission.Camera, true);
        Assert.Equal(2, store.ForgetWhere(key => SiteScope.HostKeyBelongs(key, "google.com")));
        Assert.Equal(1, store.ForgetWhere(key => SiteScope.HostKeyBelongs(key, "exemplo.pt")));
        Assert.Equal(["notgoogle.com"], store.AllSites().Keys);
    }

    // Na janela privada as decisões ficam em memória: nada chega à base do perfil, e acabam com a janela.
    [Fact]
    public void PrivatePermissions_StayInMemory()
    {
        var profile = new SitePermissionStore(_path);
        var memory = SitePermissionStore.InMemory();
        memory.Set("site-x.com", SitePermission.Camera, true);
        Assert.True(memory.Get("site-x.com", SitePermission.Camera));
        Assert.Null(profile.Get("site-x.com", SitePermission.Camera));
        Assert.Null(SitePermissionStore.InMemory().Get("site-x.com", SitePermission.Camera));
        memory.Close();
        Assert.Contains("SitePermissionStore.InMemory()", Source("MainWindow.Permissions.cs"));
    }

    [Fact]
    public void LocalNetworkGrants_ForgetWhere_OnlyThatSiteAndContext()
    {
        LocalNetworkGrants.Allow("rev50-a.exemplo.pt:8080", isPrivate: false);
        LocalNetworkGrants.Allow("rev50-b.exemplo.pt", isPrivate: true);
        LocalNetworkGrants.Allow("rev50-outro.pt", isPrivate: false);
        LocalNetworkGrants.ForgetWhere(key => SiteScope.HostKeyBelongs(key, "exemplo.pt"), isPrivate: false);
        Assert.False(LocalNetworkGrants.IsAllowed("rev50-a.exemplo.pt:8080", false));
        Assert.True(LocalNetworkGrants.IsAllowed("rev50-b.exemplo.pt", true));
        Assert.True(LocalNetworkGrants.IsAllowed("rev50-outro.pt", false));
        LocalNetworkGrants.Forget("rev50-b.exemplo.pt");
        LocalNetworkGrants.Forget("rev50-outro.pt");
    }

    // O cartão do cadeado soma o localStorage (que a quota do Chromium não conta).
    [Fact]
    public void LocalStorageBytes_CountsKeysAndValuesInUtf16()
    {
        using var json = JsonDocument.Parse("""{"entries":[["_ga","GA1.2.3"],["id","x"]]}""");
        Assert.Equal(2 * (3 + 7 + 2 + 1), SiteScope.LocalStorageBytes(json.RootElement));
        using var empty = JsonDocument.Parse("""{"entries":[]}""");
        Assert.Equal(0, SiteScope.LocalStorageBytes(empty.RootElement));
        using var odd = JsonDocument.Parse("""{"nada":1}""");
        Assert.Equal(0, SiteScope.LocalStorageBytes(odd.RootElement));
    }

    // Com o Escudo desligado o limite do referrer não se aplica: o cartão não o promete.
    [Fact]
    public void SiteKnowledge_WithShieldOff_DoesNotPromiseTheReferrerCap()
    {
        var on = SiteKnowledge.Describe(new SiteKnowledge.Facts([], 0, 0, 0, 0, "https://site.pt/", 0, T0));
        var off = SiteKnowledge.Describe(new SiteKnowledge.Facts([], 0, 0, 0, 0, "https://site.pt/", 0, T0, ShieldOff: true));
        Assert.Contains(on, l => l.Title == "Referrer enviado: só a origem");
        Assert.Contains(off, l => l.Title == "Referrer: o que a página decidir");
        Assert.DoesNotContain(off, l => l.Title.StartsWith("Referrer enviado", StringComparison.Ordinal));
    }

    // Qualquer página escreve na consola: sem o segredo do processo, a mensagem não é um clique (nem «visita a sério»).
    [Fact]
    public void ActivationReport_NeedsTheProcessSecret()
    {
        Assert.False(PopupPolicy.IsActivationReport("\u0001eteris-click -", out _, out _));
        Assert.False(PopupPolicy.IsActivationReport("\u0001eteris-click 00000000000000000000000000000000 -", out _, out _));
        Assert.True(PopupPolicy.IsActivationReport(PopupPolicy.ActivationPrefix + "-", out _, out _));
        Assert.Equal(32, PopupPolicy.ActivationNonce.Length);
        Assert.Contains(PopupPolicy.ActivationNonce, PopupPolicy.ActivationScript);
        Assert.Contains("'use strict'", PopupPolicy.ActivationScript);
        Assert.DoesNotContain("__NONCE__", PopupPolicy.ActivationScript);
    }

    // A janela privada usa a língua com que o motor arrancou (a do Accept-Language), não a das Definições por aplicar.
    [Fact]
    public void PrivateContext_UsesTheLanguageTheEngineStartedWith()
        => Assert.Contains("EngineHardening.StartedWithLanguage", Source("MainWindow.Private.cs"));

    // Um clique na faixa de abas leva o teclado para a moldura (Ctrl+Z depois de fechar pelo «x» é do Éteris).
    [Fact]
    public void TabStripClick_MovesTheKeyboardToTheFrame()
    {
        var code = Source("MainWindow.xaml.cs");
        var handler = code[code.IndexOf("private void TabItem_PreviewMouseDown", StringComparison.Ordinal)..];
        Assert.Contains("FocusFrame();", handler[..handler.IndexOf("e.ChangedButton", StringComparison.Ordinal)]);
    }

    // O cartão do cadeado rola por dentro e nunca passa do fundo do ecrã; a confirmação de Triturar vem à vista.
    [Fact]
    public void SiteInfoCard_ScrollsAndFitsTheScreen()
    {
        var xaml = Source("MainWindow.xaml");
        var popup = xaml[xaml.IndexOf("x:Name=\"SiteInfoPopup\"", StringComparison.Ordinal)..];
        popup = popup[..popup.IndexOf("</Popup>", StringComparison.Ordinal)];
        Assert.Contains("<ScrollViewer x:Name=\"SiteInfoScroll\"", popup);
        Assert.Contains("SiteInfoShredConfirm", popup[popup.IndexOf("<ScrollViewer", StringComparison.Ordinal)..]);
        var code = Source("MainWindow.SiteInfo.cs");
        Assert.Contains("SiteInfoScroll.MaxHeight", code);
        Assert.Contains("SiteInfoShredConfirm.BringIntoView()", code);
    }

    [Fact]
    public void EndPrivateSession_SaysThePermissionsGoToo()
        => Assert.Contains("e as permissões dela", PrivateSession.Confirmation(1, 0));
}
