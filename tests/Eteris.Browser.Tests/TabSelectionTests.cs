using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Multi-seleção de abas (Ctrl/Shift+clique) e «Copiar links» em bloco.
public class TabSelectionTests
{
    private static TabsViewModel With(int count)
    {
        var vm = new TabsViewModel();
        for (var i = 0; i < count; i++) vm.NewTab($"https://s{i}.pt/");
        return vm;
    }

    [Fact]
    public void Decide_ShiftWinsOverCtrl()
    {
        Assert.Equal(SelectionGesture.Activate, TabSelection.Decide(ctrl: false, shift: false));
        Assert.Equal(SelectionGesture.Toggle, TabSelection.Decide(ctrl: true, shift: false));
        Assert.Equal(SelectionGesture.Range, TabSelection.Decide(ctrl: false, shift: true));
        Assert.Equal(SelectionGesture.Range, TabSelection.Decide(ctrl: true, shift: true));
    }

    [Fact]
    public void Range_GoesBothWays_InStripOrder()
    {
        var vm = With(5);
        Assert.Equal([vm.Tabs[1], vm.Tabs[2], vm.Tabs[3]], TabSelection.Range(vm.Tabs, vm.Tabs[1], vm.Tabs[3]));
        Assert.Equal([vm.Tabs[1], vm.Tabs[2], vm.Tabs[3]], TabSelection.Range(vm.Tabs, vm.Tabs[3], vm.Tabs[1]));
        Assert.Equal([vm.Tabs[2]], TabSelection.Range(vm.Tabs, null, vm.Tabs[2]));
        Assert.Empty(TabSelection.Range(vm.Tabs, vm.Tabs[0], new BrowserTab("https://x.pt/")));
    }

    [Fact]
    public void Range_SkipsTabsHiddenByACollapsedGroup()
    {
        var vm = With(4);
        var group = vm.NewGroup(vm.Tabs[1]);
        vm.AddToGroup(vm.Tabs[2], group);
        vm.Active = vm.Tabs[0];
        vm.ToggleCollapse(group);
        var range = TabSelection.Range(vm.Tabs, vm.Tabs[0], vm.Tabs[3]);
        Assert.DoesNotContain(vm.Tabs[2], range);
        Assert.Equal(3, range.Count);
    }

    [Fact]
    public void BulkActions_NeedTwoSelected_IncludingTheClickedOne()
    {
        var vm = With(3);
        vm.Tabs[0].IsMultiSelected = true;
        Assert.False(TabSelection.OffersBulkActions(TabSelection.Selected(vm.Tabs), vm.Tabs[0]));
        vm.Tabs[1].IsMultiSelected = true;
        Assert.True(TabSelection.OffersBulkActions(TabSelection.Selected(vm.Tabs), vm.Tabs[1]));
        Assert.False(TabSelection.OffersBulkActions(TabSelection.Selected(vm.Tabs), vm.Tabs[2]));
        Assert.Equal("2 abas", TabSelection.Count(2));
        Assert.Equal("1 aba", TabSelection.Count(1));
    }

    [Fact]
    public void LinkFormats_ListsTitlesAndAddresses_AsTextAndHtml()
    {
        var links = new[] { ("Loja <A&B>", "https://a.pt/?q=1&r=2"), ("", "https://b.pt/") };
        Assert.Equal("Loja <A&B>\nhttps://a.pt/?q=1&r=2\n\nhttps://b.pt/", LinkFormats.TitlesAndAddresses(links));
        Assert.Equal("<ul><li><a href=\"https://a.pt/?q=1&amp;r=2\">Loja &lt;A&amp;B&gt;</a></li><li><a href=\"https://b.pt/\">https://b.pt/</a></li></ul>",
            LinkFormats.HtmlList(links));
    }

    [Fact]
    public void ClipboardHtml_HeaderOffsetsPointAtTheFragment_InUtf8Bytes()
    {
        var fragment = "<b>olá</b>";
        var cf = LinkFormats.ClipboardHtml(fragment);
        var bytes = System.Text.Encoding.UTF8.GetBytes(cf);
        int Field(string name) => int.Parse(System.Text.RegularExpressions.Regex.Match(cf, name + @":(\d+)").Groups[1].Value);
        var startFragment = Field("StartFragment");
        var endFragment = Field("EndFragment");
        Assert.Equal(fragment, System.Text.Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));
        Assert.StartsWith("<html>", System.Text.Encoding.UTF8.GetString(bytes, Field("StartHTML"), 6));
        Assert.Equal(bytes.Length, Field("EndHTML"));
    }
}
