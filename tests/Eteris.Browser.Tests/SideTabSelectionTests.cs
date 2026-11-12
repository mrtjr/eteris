using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// UIX-02: com a lista lateral ligada em dois sentidos, o filtro tirava a aba ativa da lista e o WPF devolvia
// `Active = null` (ou, com o item corrente sincronizado, trocava de aba). A lista só lê a aba ativa.
[Collection(WpfCollection.Name)]
public class SideTabSelectionTests
{
    [Fact]
    public void Filter_NeverChangesTheActiveTab() => StaRunner.Run(() =>
    {
        var vm = new TabsViewModel();
        vm.Restore(["https://correio.test/", "https://y.test/", "https://z.test/"], activeIndex: 0);
        var active = vm.Active;
        var view = new ListCollectionView(vm.Tabs);
        var list = new ListBox { DataContext = vm };
        SideTabSelection.Attach(list, view);
        Assert.Same(view, list.ItemsSource);
        Assert.False(list.IsSynchronizedWithCurrentItem);
        Assert.Equal(BindingMode.OneWay, list.GetBindingExpression(Selector.SelectedItemProperty)!.ParentBinding.Mode);
        view.Filter = item => ((BrowserTab)item).Address.Contains("y.test");
        SideTabSelection.Refresh(list);
        Assert.Same(active, vm.Active);
        view.Filter = _ => false;
        SideTabSelection.Refresh(list);
        Assert.Same(active, vm.Active);
        view.Filter = null;
        SideTabSelection.Refresh(list);
        Assert.Same(active, vm.Active);
    }, "SideTabSelection");
}
