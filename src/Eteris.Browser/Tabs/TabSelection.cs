namespace Eteris.Browser.Tabs;

// O que um clique numa aba faz consoante as teclas: ativa, junta/tira da seleção ou seleciona um intervalo.
public enum SelectionGesture { Activate, Toggle, Range }

// Multi-seleção de abas (Ctrl+clique, Shift+clique) e as ações em bloco. Lógica sem interface: o MainWindow
// marca BrowserTab.IsMultiSelected e o menu da aba oferece as ações quando há duas ou mais selecionadas.
public static class TabSelection
{
    public static SelectionGesture Decide(bool ctrl, bool shift)
        => shift ? SelectionGesture.Range : ctrl ? SelectionGesture.Toggle : SelectionGesture.Activate;

    // As abas entre a âncora (normalmente a ativa) e o alvo, pela ordem da faixa, inclusive — as escondidas por
    // grupos encolhidos não contam (não se vêem, não se selecionam sem querer).
    public static IReadOnlyList<BrowserTab> Range(IReadOnlyList<BrowserTab> tabs, BrowserTab? anchor, BrowserTab target)
    {
        var a = anchor is null ? -1 : tabs.IndexOf(anchor);
        var b = tabs.IndexOf(target);
        if (b < 0) return [];
        if (a < 0) a = b;
        var (from, to) = a <= b ? (a, b) : (b, a);
        return tabs.Skip(from).Take(to - from + 1).Where(t => !t.IsHiddenByGroup).ToList();
    }

    public static IReadOnlyList<BrowserTab> Selected(IEnumerable<BrowserTab> tabs) => tabs.Where(t => t.IsMultiSelected).ToList();

    // As ações em bloco só fazem sentido com duas ou mais abas, e a aba clicada tem de ser uma delas.
    public static bool OffersBulkActions(IReadOnlyList<BrowserTab> selected, BrowserTab clicked)
        => selected.Count >= 2 && selected.Contains(clicked);

    public static string Count(int n) => n == 1 ? "1 aba" : $"{n} abas";

    private static int IndexOf(this IReadOnlyList<BrowserTab> tabs, BrowserTab tab)
    {
        for (var i = 0; i < tabs.Count; i++)
            if (ReferenceEquals(tabs[i], tab)) return i;
        return -1;
    }
}
