using System.Windows.Media;

namespace Eteris.Browser.Tabs;

// Âmbitos do radar de abas, pela ordem do seletor «Mostrar».
public enum RadarScope { All, Pinned, Audible, Sleeping, Duplicates, Closed, Done }

// Ordenações do radar, pela ordem do seletor «Ordenar». Strip = ordem da faixa de abas (sem reordenar).
public enum RadarSort { Recent, Site, Strip }

// Uma linha da lista do radar: aba aberta (Tab) ou entrada das fechadas recentemente (Closed).
public sealed record RadarRow(BrowserTab? Tab, ClosedTab? Closed, string Title, string Address,
    string Subtitle, string Badge, Brush BadgeBrush, ImageSource? Image, string Status, string? Note = null, Data.DoneTab? Done = null)
{
    public object Identity => (object?)Tab ?? (object?)Closed ?? Done!;
}

// Lógica do radar de abas sem interface: filtros, duplicadas, ordenações e texto de estado.
// O UserControl (TabRadarView) só liga estas funções aos controlos.
public static class TabRadar
{
    public static IReadOnlyList<RadarRow> Rows(IReadOnlyList<BrowserTab> tabs, BrowserTab? active,
        IReadOnlyList<ClosedTab> closed, RadarScope scope, RadarSort sort, string query, IReadOnlyList<Data.DoneTab>? done = null)
    {
        query = query.Trim();
        var duplicates = Duplicates(tabs);

        var rows = scope == RadarScope.Closed
            ? closed.Select(FromClosed)
            : scope == RadarScope.Done
            ? (done ?? []).Select(FromDone)
            : tabs.Where(tab => scope switch
                {
                    RadarScope.Pinned => tab.IsPinned,
                    RadarScope.Audible => tab.IsAudible && !tab.IsMuted,
                    RadarScope.Sleeping => tab.IsSleeping,
                    RadarScope.Duplicates => WebAddress(tab.Address) is { } address && duplicates.ContainsKey(address),
                    _ => true,
                })
                .Select(tab => FromTab(tab, ReferenceEquals(active, tab),
                    duplicates.GetValueOrDefault(WebAddress(tab.Address) ?? string.Empty)));

        if (query.Length > 0)
            // Uma aba com nome próprio continua a aparecer ao pesquisar pelo título da página.
            rows = rows.Where(row => TabFilter.Matches($"{row.Title} {row.Subtitle} {row.Tab?.Title} {row.Note}", row.Address, query));

        if (sort == RadarSort.Site)
            rows = rows.OrderBy(row => Site(row.Address), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase);
        // As fechadas e as feitas já vêm da mais recente para a mais antiga; nas abertas a ativa fica sempre primeiro.
        else if (sort == RadarSort.Recent && scope is not (RadarScope.Closed or RadarScope.Done))
            rows = rows.OrderByDescending(row => ReferenceEquals(row.Tab, active))
                .ThenByDescending(row => row.Tab?.LastActiveUtc);

        return rows.ToArray();
    }

    // Linha por baixo da lista: contagem ou o motivo de estar vazia.
    public static string Status(int count, RadarScope scope, string query)
        => count switch
        {
            0 when query.Trim().Length > 0 => "Sem resultados. Experimente outro termo ou filtro.",
            0 when scope == RadarScope.Closed => "Ainda não fechou abas nesta janela.",
            0 when scope == RadarScope.Done => "Nada marcado como feito hoje. «Feito» está no menu da aba.",
            0 => "Nenhuma aba neste filtro.",
            1 => "1 aba nesta vista",
            _ => $"{count} abas nesta vista",
        };

    // Duplicadas: o mesmo endereço web (normalizado pelo Uri) em mais de uma aba. Páginas internas não contam.
    public static IReadOnlyDictionary<string, int> Duplicates(IEnumerable<BrowserTab> tabs)
        => tabs.Select(tab => WebAddress(tab.Address)).OfType<string>()
            .GroupBy(address => address, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    public static string? WebAddress(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;

    // Host tal como está no endereço (mantém «www.»); páginas eteris:// e endereços inválidos são «Éteris».
    public static string Site(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme != "eteris" && uri.Host.Length > 0
            ? uri.Host
            : "Éteris";

    public static RadarRow FromTab(BrowserTab tab, bool active, int duplicates)
    {
        var status = new List<string>();
        if (active) status.Add("Em uso");
        if (tab.IsPinned) status.Add("Fixada");
        if (tab.IsLocked) status.Add("Trancada");
        if (tab.IsSleeping) status.Add("A dormir");
        if (tab.IsMuted) status.Add("Silenciada");
        else if (tab.IsAudible) status.Add("Com som");
        if (duplicates > 1) status.Add($"{duplicates} com o mesmo endereço");
        var site = Site(tab.Address);
        var subtitle = tab.Group is { } group ? $"{site} · {group.Name}" : site;
        return new(tab, null, tab.CustomTitle ?? (string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title), tab.Address,
            subtitle, tab.Monogram ?? "É", tab.MonogramBrush, tab.Favicon, string.Join(" · ", status), tab.Note);
    }

    public static RadarRow FromDone(Data.DoneTab tab)
    {
        var site = Site(tab.Url);
        var when = tab.DoneUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new(null, null, string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title, tab.Url,
            site, SiteBadge.Letter(tab.Url) ?? "É", Brushes.SeaGreen, null, $"Feita às {when}", null, tab);
    }

    public static RadarRow FromClosed(ClosedTab tab)
    {
        var site = Site(tab.Address);
        return new(null, tab, string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title, tab.Address,
            site, SiteBadge.Letter(tab.Address) ?? "É", Brushes.SlateGray, null, "Fechada recentemente");
    }
}
