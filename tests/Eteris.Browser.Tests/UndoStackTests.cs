using Eteris.Browser.Palette;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Avisos com ação e Ctrl+Z: a pilha de anulação, o tempo dos avisos e o regresso das abas ao seu lugar.
public class UndoStackTests
{
    [Fact]
    public void UndoLast_RunsTheMostRecentOnce()
    {
        var stack = new UndoStack();
        var log = new List<string>();
        stack.Push("a", () => log.Add("a"));
        stack.Push("b", () => log.Add("b"));
        Assert.Equal("b", stack.UndoLast()!.Label);
        Assert.Equal("a", stack.UndoLast()!.Label);
        Assert.Null(stack.UndoLast());
        Assert.Equal(["b", "a"], log);
    }

    // O «Anular» de um aviso já anulado pelo Ctrl+Z não corre outra vez.
    [Fact]
    public void Undo_FromTheToast_OnlyWhileStillThere()
    {
        var stack = new UndoStack();
        var runs = 0;
        var entry = stack.Push("Feito «x»", () => runs++);
        Assert.NotNull(stack.UndoLast());
        Assert.False(stack.Undo(entry));
        Assert.Equal(1, runs);
    }

    // O «Anular» de um aviso antigo tira essa entrada e deixa as outras.
    [Fact]
    public void Undo_SpecificEntry_LeavesTheRest()
    {
        var stack = new UndoStack();
        var first = stack.Push("1", () => { });
        stack.Push("2", () => { });
        Assert.True(stack.Undo(first));
        Assert.Equal("2", stack.Peek!.Label);
        Assert.Equal(1, stack.Count);
    }

    [Fact]
    public void Stack_KeepsTheLatestMax()
    {
        var stack = new UndoStack();
        for (var i = 0; i < UndoStack.Max + 5; i++) stack.Push(i.ToString(), () => { });
        Assert.Equal(UndoStack.Max, stack.Count);
        Assert.Equal((UndoStack.Max + 4).ToString(), stack.Peek!.Label);
    }

    [Fact]
    public void Toast_WithActions_StaysLonger()
    {
        Assert.True(ToastPolicy.Hold(1) >= TimeSpan.FromSeconds(5));
        Assert.True(ToastPolicy.Hold(0) < TimeSpan.FromSeconds(3));
        Assert.Equal(2, ToastPolicy.MaxActions);
    }

    [Fact]
    public void Quote_CutsLongTitles()
    {
        Assert.Equal("«Wikipédia»", ToastPolicy.Quote("Wikipédia"));
        var quoted = ToastPolicy.Quote(new string('a', 100), 20);
        Assert.Equal(22, quoted.Length);
        Assert.EndsWith("…»", quoted);
    }

    // «Feito» e «Adiar» anulados: a aba volta ao mesmo lugar, com o nome próprio, a nota e o grupo.
    [Fact]
    public void PutBack_RestoresPlaceIdentityAndGroup()
    {
        var vm = new TabsViewModel();
        var a = vm.NewTab("https://a.pt/");
        var b = vm.NewTab("https://b.pt/");
        var c = vm.NewTab("https://c.pt/");
        var group = vm.NewGroup(b);
        vm.AddToGroup(c, group);
        b.SetIdentity("Leitura", "📚", 2, "acabar hoje");
        var snapshot = vm.Snapshot(b);
        vm.CloseQuietly(b);
        Assert.Equal(2, vm.Tabs.Count);

        var back = vm.PutBack(snapshot);
        Assert.Equal(1, vm.Tabs.IndexOf(back));
        Assert.Equal("https://b.pt/", back.Address);
        Assert.Equal("Leitura", back.CustomTitle);
        Assert.Equal("acabar hoje", back.Note);
        Assert.Same(group, back.Group);
        Assert.Same(back, vm.Active);
        Assert.Same(a, vm.Tabs[0]);
    }

    // Era a última aba: a aba nova vazia que ficou no lugar dela sai ao repô-la.
    [Fact]
    public void PutBack_ReplacesTheEmptyNewTab()
    {
        var vm = new TabsViewModel();
        var only = vm.NewTab("https://so.pt/");
        var snapshot = vm.Snapshot(only);
        vm.CloseQuietly(only);
        vm.NewTab(Eteris.Browser.Browser.NewTabPage.Url);
        vm.PutBack(snapshot);
        Assert.Equal(["https://so.pt/"], vm.Tabs.Select(t => t.Address));
    }

    // Fechos em bloco: «Anular» reabre só as que essa ação fechou, cada uma no seu lugar.
    [Fact]
    public void ClosedSince_AndReopenAll_RestoreABatch()
    {
        var vm = new TabsViewModel();
        var tabs = new[] { "https://1.pt/", "https://2.pt/", "https://3.pt/", "https://4.pt/" }.Select(u => vm.NewTab(u)).ToList();
        vm.Close(tabs[0]);
        var before = vm.RecentlyClosed;
        vm.Close(tabs[1]);
        vm.Close(tabs[3]);
        var batch = vm.ClosedSince(before);
        Assert.Equal(["https://2.pt/", "https://4.pt/"], batch.Select(c => c.Address));
        Assert.Equal(2, vm.ReopenAll(batch));
        Assert.Equal(["https://2.pt/", "https://3.pt/", "https://4.pt/"], vm.Tabs.Select(t => t.Address));
        // A fechada antes da ação continua nas fechadas recentes.
        Assert.Equal(["https://1.pt/"], vm.RecentlyClosed.Select(c => c.Address));
    }

    // Ctrl+Shift+T entretanto reabriu a aba: o Ctrl+Z já não a duplica.
    [Fact]
    public void ReopenAll_SkipsTabsAlreadyReopened()
    {
        var vm = new TabsViewModel();
        var x = vm.NewTab("https://x.pt/");
        vm.NewTab("https://y.pt/");
        var before = vm.RecentlyClosed;
        vm.Close(x);
        var batch = vm.ClosedSince(before);
        vm.ReopenClosed();
        Assert.Equal(0, vm.ReopenAll(batch));
        Assert.Single(vm.Tabs, t => t.Address == "https://x.pt/");
    }

    [Fact]
    public void ShortcutSheet_ListsCtrlZ()
        => Assert.Contains(ShortcutSheet.Groups.SelectMany(g => g.Items), s => s.Keys == "Ctrl+Z" && s.Action.Contains("Anular"));
}
