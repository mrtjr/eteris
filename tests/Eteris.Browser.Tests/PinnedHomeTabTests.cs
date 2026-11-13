using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// A casa na aba (BrowserTab.Home) e no TabsViewModel: nasce ao fixar (o endereço confirmado), é esquecida ao desafixar,
// adota a 1.ª página web numa fixada sem casa, acompanha só o http→https e o www. ao voltar, e vai com a aba para o
// «Anular» e para os Espaços.
public class PinnedHomeTabTests
{
    private static BrowserTab Pinned(string address) => new(address) { IsPinned = true };

    private static TabsViewModel WithTabs(params string[] addresses)
    {
        var vm = new TabsViewModel();
        vm.Restore(addresses, 0);
        return vm;
    }

    [Fact]
    public void Pinning_SavesTheConfirmedAddressAsHome()
    {
        var tab = new BrowserTab("https://ana:segredo@exemplo.pt/caixa#topo") { PendingAddress = "https://outro.pt/" };
        Assert.Null(tab.Home);
        tab.IsPinned = true;
        Assert.Equal("https://exemplo.pt/caixa#topo", tab.Home);
        Assert.False(tab.IsAwayFromHome);
    }

    [Fact]
    public void PinningANewTab_HasNoHome_ThenAdoptsTheFirstWebPage()
    {
        var tab = Pinned(Browser.NewTabPage.Url);
        Assert.Null(tab.Home);
        Assert.False(tab.IsAwayFromHome);
        tab.Address = "eteris://history/";
        Assert.Null(tab.Home);
        tab.Address = "https://exemplo.pt/a";
        Assert.Equal("https://exemplo.pt/a", tab.Home);
        tab.Address = "https://exemplo.pt/b";
        Assert.Equal("https://exemplo.pt/a", tab.Home);
        Assert.True(tab.IsAwayFromHome);
    }

    [Fact]
    public void AnUnpinnedTab_NeverHasAHome()
    {
        var tab = new BrowserTab("https://exemplo.pt/");
        tab.Address = "https://exemplo.pt/a";
        Assert.Null(tab.Home);
        tab.Home = "https://exemplo.pt/";
        Assert.Null(tab.Home);
        Assert.False(tab.IsAwayFromHome);
        Assert.Null(tab.HomeLabel);
        Assert.Null(tab.HomeHint);
    }

    [Fact]
    public void Unpinning_ForgetsTheHome_AndRepinningTakesThatMoment()
    {
        var vm = WithTabs("https://exemplo.pt/", "https://b.pt/");
        var tab = vm.Tabs[0];
        vm.TogglePin(tab);
        Assert.Equal("https://exemplo.pt/", tab.Home);
        tab.Address = "https://exemplo.pt/outra";
        vm.TogglePin(tab);
        Assert.False(tab.IsPinned);
        Assert.Null(tab.Home);
        vm.TogglePin(tab);
        Assert.Equal("https://exemplo.pt/outra", tab.Home);
        Assert.False(tab.IsAwayFromHome);
    }

    [Fact]
    public void IsAwayFromHome_FollowsTheAddress()
    {
        var tab = Pinned("https://exemplo.pt/caixa");
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Address = "https://exemplo.pt/outra";
        Assert.True(tab.IsAwayFromHome);
        Assert.Contains(nameof(BrowserTab.IsAwayFromHome), raised);
        Assert.Contains(nameof(BrowserTab.StatusLine), raised);

        tab.Address = "https://exemplo.pt/caixa#secao";
        Assert.False(tab.IsAwayFromHome);
        tab.Address = "https://exemplo.pt/caixa";
        Assert.False(tab.IsAwayFromHome);
        Assert.Equal("https://exemplo.pt/caixa", tab.Home);
    }

    [Fact]
    public void StatusLine_SaysAwayFromHome()
    {
        var tab = Pinned("https://www.exemplo.pt/");
        Assert.Equal("Fixada", tab.StatusLine);
        tab.Address = "https://www.exemplo.pt/outra";
        Assert.Equal("Fixada · Fora de casa (Alt+Home volta a exemplo.pt)", tab.StatusLine);
        Assert.Equal("exemplo.pt", tab.HomeLabel);
        Assert.Equal("Voltar a exemplo.pt", tab.HomeHint);
    }

    [Fact]
    public void GoingHome_AbsorbsARedirectOfTheSameSite_OnlyOnce()
    {
        var tab = Pinned("http://exemplo.pt/");
        tab.Address = "http://exemplo.pt/outra";
        tab.BeginGoingHome();
        Assert.True(tab.HomeLandingPending);
        tab.Address = "https://www.exemplo.pt/";
        Assert.Equal("https://www.exemplo.pt/", tab.Home);
        Assert.False(tab.IsAwayFromHome);
        Assert.False(tab.HomeLandingPending);

        tab.Address = "https://exemplo.pt/";
        Assert.Equal("https://www.exemplo.pt/", tab.Home);
        Assert.True(tab.IsAwayFromHome);
    }

    // A chegada à própria casa (com âncora ou rastreio) não a reescreve, e a bandeira continua até ao fim da carga: a troca
    // de motor escreve o endereço pedido antes de o motor o confirmar.
    [Fact]
    public void GoingHome_ArrivingAtTheHomeItself_KeepsTheSavedHome()
    {
        var tab = Pinned("http://exemplo.pt/a#topo");
        tab.Address = "http://exemplo.pt/b";
        tab.BeginGoingHome();
        tab.Address = "http://exemplo.pt/a?utm_source=x";
        Assert.Equal("http://exemplo.pt/a#topo", tab.Home);
        Assert.True(tab.HomeLandingPending);
        tab.Address = "https://exemplo.pt/a";
        Assert.Equal("https://exemplo.pt/a", tab.Home);
        Assert.False(tab.HomeLandingPending);
    }

    [Fact]
    public void GoingHome_IgnoresALoginOnTheSameSite()
    {
        var tab = Pinned("https://app.exemplo.pt/painel");
        tab.Address = "https://app.exemplo.pt/definicoes";
        tab.BeginGoingHome();
        tab.Address = "https://app.exemplo.pt/login?next=%2Fpainel";
        Assert.Equal("https://app.exemplo.pt/painel", tab.Home);
        Assert.True(tab.IsAwayFromHome);
        Assert.False(tab.HomeLandingPending);
    }

    [Fact]
    public void GoingHome_IgnoresALoginOnAnotherSite()
    {
        var tab = Pinned("https://app.exemplo.pt/painel");
        tab.Address = "https://app.exemplo.pt/definicoes";
        tab.BeginGoingHome();
        tab.Address = "https://contas.exemplo.com/entrar";
        Assert.Equal("https://app.exemplo.pt/painel", tab.Home);
        Assert.True(tab.IsAwayFromHome);
    }

    [Fact]
    public void GoingHome_EndsWithTheLoad()
    {
        var tab = Pinned("http://exemplo.pt/");
        tab.Address = "http://exemplo.pt/outra";
        tab.IsLoading = true;
        tab.BeginGoingHome();
        tab.IsLoading = false;
        Assert.False(tab.HomeLandingPending);
        tab.Address = "https://exemplo.pt/";
        Assert.Equal("http://exemplo.pt/", tab.Home);
        Assert.True(tab.IsAwayFromHome);
    }

    // O fim da carga da página anterior, enquanto o pedido da casa ainda não começou (LoadUrl espera pelos scripts), não
    // desarma a bandeira; o fim da carga pedida (ou um Esc) desarma.
    [Fact]
    public void GoingHome_TheOldPageFinishing_DoesNotEndIt()
    {
        var tab = Pinned("http://exemplo.pt/");
        tab.Address = "http://exemplo.pt/outra";
        tab.IsLoading = true;
        tab.BeginGoingHome();
        tab.PendingAddress = "http://exemplo.pt/";
        tab.PendingLoadStarted = false;
        tab.IsLoading = false;
        Assert.True(tab.HomeLandingPending);
        tab.PendingLoadStarted = true;
        tab.IsLoading = true;
        tab.IsLoading = false;
        Assert.False(tab.HomeLandingPending);
    }

    [Fact]
    public void PutBack_KeepsTheHome()
    {
        var vm = WithTabs("https://exemplo.pt/", "https://b.pt/");
        var tab = vm.Tabs[0];
        vm.TogglePin(tab);
        tab.Address = "https://exemplo.pt/outra";
        var snapshot = vm.Snapshot(tab);
        vm.CloseQuietly(tab);
        var back = vm.PutBack(snapshot);
        Assert.True(back.IsPinned);
        Assert.Equal("https://exemplo.pt/", back.Home);
        Assert.True(back.IsAwayFromHome);
    }

    [Fact]
    public void MoveToWorkspace_KeepsTheHome()
    {
        var vm = WithTabs("https://exemplo.pt/", "https://b.pt/");
        var work = vm.NewWorkspace("Trabalho", activate: false);
        var tab = vm.Tabs[0];
        vm.TogglePin(tab);
        tab.Address = "https://exemplo.pt/outra";
        Assert.True(vm.MoveToWorkspace(tab, work));
        Assert.Equal("https://exemplo.pt/", tab.Home);
        var (spaces, _) = vm.WorkspaceSnapshot();
        var stored = Assert.Single(spaces!, s => s.Tabs is not null).Tabs!;
        Assert.Equal("https://exemplo.pt/", Assert.Single(stored.Metas!).Home);
    }

    [Fact]
    public void MetaSnapshot_WritesHomeOnlyForPinnedTabs()
    {
        var pinned = Pinned("https://a.pt/");
        pinned.Address = "https://a.pt/x";
        var metas = TabsViewModel.MetaSnapshotOf([pinned, new BrowserTab("https://b.pt/")], null);
        Assert.Equal("https://a.pt/", metas[0].Home);
        Assert.Null(metas[1].Home);
    }

    [Fact]
    public void SetHome_TakesTheCleanAddress_OnlyOnPinnedTabs()
    {
        var vm = WithTabs("https://exemplo.pt/", "https://b.pt/");
        var tab = vm.Tabs[0];
        vm.TogglePin(tab);
        tab.Address = "https://ana:segredo@exemplo.pt/nova";
        Assert.True(vm.SetHome(tab, tab.Address));
        Assert.Equal("https://exemplo.pt/nova", tab.Home);
        Assert.False(tab.IsAwayFromHome);
        Assert.False(vm.SetHome(vm.Tabs[1], "https://b.pt/"));
        Assert.Null(vm.Tabs[1].Home);
    }

    // O «Anular» de «Usar este endereço como casa» guarda a aba: depois de ela sair da faixa («Feito», fechar) e voltar como
    // outra (PutBack, reabrir), a aba antiga continua fixada, mas já não é desta janela: não muda nada e não diz «Anulado».
    // Uma aba guardada noutro Espaço continua a contar.
    [Fact]
    public void SetHome_RefusesATabThatLeftTheStrip()
    {
        var vm = WithTabs("https://exemplo.pt/", "https://b.pt/");
        var tab = vm.Tabs[0];
        vm.TogglePin(tab);
        tab.Address = "https://exemplo.pt/nova";
        Assert.True(vm.SetHome(tab, tab.Address));
        var snapshot = vm.Snapshot(tab);
        vm.CloseDone(tab);
        var back = vm.PutBack(snapshot);
        Assert.False(vm.SetHome(tab, "https://exemplo.pt/"));
        Assert.Equal("https://exemplo.pt/nova", back.Home);

        vm.NewWorkspace("Trabalho");
        Assert.True(vm.SetHome(back, "https://exemplo.pt/outra"));
    }

    // O Radar diz «Fixada · fora de casa» (também numa aba de outro Espaço).
    [Fact]
    public void TheRadar_SaysAwayFromHome()
    {
        var tab = Pinned("https://exemplo.pt/");
        Assert.Equal("Fixada", TabRadar.FromTab(tab, active: false, duplicates: 1).Status);
        tab.Address = "https://exemplo.pt/outra";
        Assert.Equal("Fixada · fora de casa", TabRadar.FromTab(tab, active: false, duplicates: 1).Status);
        Assert.Equal("No Espaço «Trabalho» · Fixada · fora de casa", TabRadar.FromSpace(tab, new Workspace("Trabalho")).Status);
    }
}
