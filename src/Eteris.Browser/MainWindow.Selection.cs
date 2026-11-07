using System.Windows;
using System.Windows.Input;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Multi-seleção de abas: Ctrl+clique junta ou tira uma aba, Shift+clique seleciona da ativa até à clicada, um
// clique normal limpa. Com duas ou mais selecionadas, o menu da aba (e a paleta) oferece as ações em bloco.
// Decisões em Tabs/TabSelection.cs; os formatos de «Copiar links» em Browser/LinkFormats.cs.
public partial class MainWindow
{
    private IReadOnlyList<BrowserTab> SelectedTabs => TabSelection.Selected(Tabs.Tabs);

    // Chamado pelo clique esquerdo numa aba (fora do «x» e da etiqueta do grupo); devolve true se tratou o clique.
    private bool HandleSelectionClick(BrowserTab tab)
    {
        var gesture = TabSelection.Decide(Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        switch (gesture)
        {
            case SelectionGesture.Toggle:
                // A ativa conta sempre como selecionada quando se começa a juntar outras.
                if (SelectedTabs.Count == 0 && Tabs.Active is { } active && !ReferenceEquals(active, tab)) active.IsMultiSelected = true;
                tab.IsMultiSelected = !tab.IsMultiSelected;
                return true;
            case SelectionGesture.Range:
                ClearSelection();
                foreach (var member in TabSelection.Range(Tabs.Tabs, Tabs.Active, tab)) member.IsMultiSelected = true;
                return true;
            default:
                ClearSelection();
                return false;
        }
    }

    private void ClearSelection()
    {
        foreach (var tab in Tabs.Tabs) tab.IsMultiSelected = false;
    }

    private void AddBulkItems(DarkMenu menu, IReadOnlyList<BrowserTab> selected)
    {
        var n = selected.Count;
        var count = TabSelection.Count(n);
        menu.Item("", $"Copiar {n} links", () => CopySelectedLinks(selected))
            .Item("", $"Agrupar {count} num novo grupo", () => GroupSelected(selected), enabled: selected.Any(t => !t.IsPinned))
            .Item("", $"Mover {count} para uma nova janela", () => MoveSelectedToNewWindow(selected), enabled: n < Tabs.Tabs.Count)
            .With(menu => AddWorkspaceItems(menu, selected))
            .Item("", $"Adormecer {count}", () => SleepSelected(selected))
            .Item("", $"Trancar {count}", () => { foreach (var tab in selected) tab.IsLocked = true; })
            .Separator()
            .Item("", $"Fechar {count}", () => CloseSelected(selected))
            .Item("", "Limpar a seleção", ClearSelection);
    }

    private void CopySelectedLinks(IReadOnlyList<BrowserTab> selected)
    {
        var links = selected.Select(t => (t.DisplayTitle, Shields.UrlCleaner.Clean(t.Address))).ToList();
        try
        {
            var data = new DataObject();
            data.SetText(LinkFormats.TitlesAndAddresses(links));
            data.SetData(DataFormats.Html, LinkFormats.ClipboardHtml(LinkFormats.HtmlList(links)));
            Clipboard.SetDataObject(data, true);
            ShowToast($"{TabSelection.Count(links.Count)} copiadas como links (texto e HTML)");
        }
        catch (Exception) { }
        ClearSelection();
    }

    private void GroupSelected(IReadOnlyList<BrowserTab> selected)
    {
        var members = selected.Where(t => !t.IsPinned).ToList();
        if (members.Count == 0) return;
        var group = Tabs.NewGroup(members[0]);
        foreach (var tab in members.Skip(1)) Tabs.AddToGroup(tab, group);
        ClearSelection();
        ShowToast($"«{group.Name}» criado com {TabSelection.Count(members.Count)} — botão direito na etiqueta para o nome e a cor");
    }

    private void MoveSelectedToNewWindow(IReadOnlyList<BrowserTab> selected)
    {
        if (selected.Count >= Tabs.Tabs.Count) return;
        var saved = selected.Select(t => new Data.SavedTab(t.Address, t.Title, t.CustomTitle, t.Note)).ToList();
        foreach (var tab in selected) Tabs.CloseQuietly(tab);
        OpenWindowWith(saved, 0);
    }

    private void SleepSelected(IReadOnlyList<BrowserTab> selected)
    {
        var slept = 0;
        foreach (var tab in selected.Where(t => !IsTabOnScreen(t) && !t.IsSleeping && !t.IsAudible && !t.IsLocked))
        {
            ReleaseBrowser(tab);
            tab.IsSleeping = true;
            slept++;
        }
        ClearSelection();
        ShowToast(slept == 0 ? "Nada para adormecer (a ativa, as trancadas e as que tocam som ficam acordadas)" : $"{TabSelection.Count(slept)} a dormir");
    }

    private void CloseSelected(IReadOnlyList<BrowserTab> selected)
    {
        var locked = selected.Count(t => t.IsLocked);
        foreach (var tab in selected.Where(t => !t.IsLocked).ToList())
            if (Tabs.Close(tab)) { Close(); return; }
        if (locked > 0) ShowToast(locked == 1 ? "1 aba trancada ficou aberta" : $"{locked} abas trancadas ficaram abertas");
    }
}
