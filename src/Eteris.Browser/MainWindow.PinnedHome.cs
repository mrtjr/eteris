using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Palette;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Abas fixadas com casa (Tabs/PinnedHome.cs, BrowserTab.Home): fora de casa, o ícone da aba mostra um ponto, e «Voltar a
// casa» leva-a lá — um clique no ícone da aba que já está aberta, Alt+Home, o menu da aba ou a paleta. Volta como uma
// navegação normal (LoadUrl: a omnibox, o motor de vídeo e o histórico do Voltar), por isso Alt+← regressa onde estava.
// Nunca abre aba nova nem mexe no nome próprio ou na nota.
public partial class MainWindow
{
    // A aba cujo ícone foi carregado (já era a ativa e estava fora de casa): decide-se ao largar o botão, para não chocar
    // com a ativação (ao carregar) nem com o arrasto da faixa.
    private BrowserTab? _homeClick;

    // A área do clique no ícone: na faixa do topo, a fixada inteira (44×36, o título está escondido); nas verticais
    // encolhidas, a linha toda; nas abertas, o IconHit de 24×24 à volta do ícone.
    private bool IsInsideIconHit(DependencyObject? source, FrameworkElement item)
    {
        if (item is not ListBoxItem container) return false;
        var name = ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(container), SideTabList) ? SideCollapsed ? "Row" : "IconHit" : "TabRoot";
        if (container.Template?.FindName(name, container) is not DependencyObject hit) return false;
        for (var node = source; node is not null && !ReferenceEquals(node, container); node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, hit)) return true;
        return false;
    }

    // Ao largar (sem arrasto): a mesma aba, ainda ativa e fora de casa, e o ponteiro ainda dentro da área do ícone dela.
    private void TryGoHomeFromIcon(MouseButtonEventArgs e, ListBox? list = null)
    {
        list ??= TabList;
        var tab = _homeClick;
        _homeClick = null;
        if (tab is null || !(ReferenceEquals(tab, Tabs.Active) && tab.IsAwayFromHome)) return;
        if (list.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement item
            || !IsInsideIconHit(list.InputHitTest(e.GetPosition(list)) as DependencyObject, item)) return;
        GoHome(tab);
    }

    private void SideTabList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => TryGoHomeFromIcon(e, SideTabList);

    // Alt+Home (e «Voltar a casa» na paleta): a aba ativa, ou um aviso que diz porque não.
    private void GoHomeActive()
    {
        if (Tabs.Active is not { } tab) return;
        // A paleta só tem «Fixar esta aba» numa página web (HomeCommands): na nova aba e nas páginas do Éteris, primeiro o site.
        if (!tab.IsPinned)
            ShowToast(PinnedHome.CanBeHome(tab.Address)
                ? "Só as abas fixadas têm casa: Ctrl+K › «Fixar esta aba»"
                : "Só as abas fixadas têm casa: abra um site e depois Ctrl+K › «Fixar esta aba»");
        else if (tab.Home is null) ShowToast("Esta aba fixada ainda não tem casa: a primeira página web que abrir passa a sê-lo");
        else if (!tab.IsAwayFromHome) ShowToast("Já está na casa desta aba");
        else GoHome(tab);
    }

    private void GoHome(BrowserTab tab)
    {
        if (tab.Home is not { } home || !Tabs.Tabs.Contains(tab)) return;
        // Adormecida (sem motor): o motor nasce já na casa ao ativá-la (OnActiveTabChanging), com uma só carga.
        if (tab.IsSleeping)
        {
            var from = tab.Address;
            tab.ForgetFaviconOfOtherSite(home);
            tab.Address = home;
            tab.BeginGoingHome();
            Tabs.Active = tab;
            // «Esquecer ao fechar»: ir para outro site também é sair do anterior.
            NoteSiteLeft(from, home);
            return;
        }
        if (!ReferenceEquals(Tabs.Active, tab)) Tabs.Active = tab;
        tab.BeginGoingHome();
        LoadUrl(home);
    }

    // «Usar este endereço como casa» (menu da aba, paleta), com «Anular». O endereço anterior vai com a entrada da pilha,
    // para o «Triturar» a limpar.
    private void UseAddressAsHome(BrowserTab tab)
    {
        if (!PinnedHome.CanBeHome(tab.Address)) return;
        var previous = tab.Home;
        if (!Tabs.SetHome(tab, tab.Address)) return;
        ShowUndoToast($"A casa desta aba passa a ser {tab.HomeLabel}", $"Casa nova: «{tab.HomeLabel}»",
            () => Tabs.SetHome(tab, previous), previous is null ? [] : [previous]);
    }

    // No menu da aba, logo a seguir a «Fixar aba / Desafixar aba», só nas fixadas. «Voltar a casa» funciona também numa
    // fixada de fundo: o menu é um gesto explícito.
    private void AddHomeItems(DarkMenu menu, BrowserTab tab)
    {
        if (!tab.IsPinned) return;
        menu.Item("\uE80F", "Voltar a casa", () => GoHome(tab), "Alt+Home", enabled: tab.IsAwayFromHome)
            .Item("\uE8EC", "Usar este endereço como casa", () => UseAddressAsHome(tab), enabled: tab.IsAwayFromHome && PinnedHome.CanBeHome(tab.Address));
    }

    // Na paleta (as abas não recebem foco e o menu da aba só abre com o rato: este é o caminho do teclado).
    private IEnumerable<PaletteCommand> HomeCommands(BrowserTab? tab)
    {
        if (tab is null) yield break;
        if (!tab.IsPinned)
        {
            if (PinnedHome.CanBeHome(tab.Address))
                yield return new("\uE718", "Fixar esta aba", "Fica no início da lista de abas, com casa neste endereço", null, () => Tabs.TogglePin(tab));
            yield break;
        }
        yield return new("\uE77A", "Desafixar esta aba", "Deixa de ter casa", null, () => Tabs.TogglePin(tab));
        if (!tab.IsAwayFromHome) yield break;
        yield return new("\uE80F", "Voltar a casa", tab.HomeHint!, "Alt+Home", GoHomeActive);
        if (PinnedHome.CanBeHome(tab.Address))
            yield return new("\uE8EC", "Usar este endereço como casa", $"A aba passa a voltar a {PinnedHome.Label(tab.Address)}", null, () => UseAddressAsHome(tab));
    }
}
