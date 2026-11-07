namespace Eteris.Browser.Tabs;

// Lista «▾» de todas as abas da faixa do topo (pesquisável, pela ordem da faixa) — o que o Firefox chama
// «Listar todas as abas». Só lógica; a interface está em MainWindow.TabStrip.cs.
public static class TabListMenu
{
    // A partir daqui a lista sugere passar para abas verticais, que aguentam dezenas de abas.
    public const int ManyTabs = 20;

    public static IReadOnlyList<RadarRow> Rows(IReadOnlyList<BrowserTab> tabs, BrowserTab? active, string query)
        => TabRadar.Rows(tabs, active, [], RadarScope.All, RadarSort.Strip, query);

    // Linha selecionada ao abrir: a aba ativa (Enter volta a ela, as setas partem dela); com pesquisa, o primeiro resultado.
    public static int InitialIndex(IReadOnlyList<RadarRow> rows, BrowserTab? active, string query)
    {
        if (rows.Count == 0) return -1;
        if (query.Trim().Length > 0) return 0;
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i].Tab, active)) return i;
        return 0;
    }

    public static string Status(int shown, int total, string query)
        => query.Trim().Length == 0
            ? total == 1 ? "1 aba aberta" : $"{total} abas abertas · pela ordem da faixa"
            : shown == 0 ? "Nenhuma aba corresponde" : $"{shown} de {total} abas";

    public static bool SuggestsVerticalTabs(int count, bool verticalTabs) => !verticalTabs && count >= ManyTabs;

    // Índice seguinte ao andar com as setas; dá a volta nas pontas.
    public static int Move(int index, int delta, int count)
        => count <= 0 ? -1 : ((index + delta) % count + count) % count;
}
