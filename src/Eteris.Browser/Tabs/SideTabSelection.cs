using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Eteris.Browser.Tabs;

// A lista de abas da barra lateral, com a vista filtrada: só LÊ a aba ativa. Em dois sentidos, o filtro que tirava a aba
// ativa da lista fazia o WPF escrever `Active = null` (a janela ficava sem aba ativa) e, com o item corrente sincronizado,
// o Enter e as setas trocavam de aba sem ninguém o pedir. Ativar uma aba é feito pelo clique (TabItem_PreviewMouseDown).
public static class SideTabSelection
{
    public static void Attach(ListBox list, ListCollectionView view)
    {
        list.ItemsSource = view;
        list.IsSynchronizedWithCurrentItem = false;
        list.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(TabsViewModel.Active)) { Mode = BindingMode.OneWay });
    }

    // Com a ligação num só sentido, o realce da aba ativa não volta sozinho quando o filtro a devolve à lista.
    public static void Refresh(ListBox list) => list.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
}
