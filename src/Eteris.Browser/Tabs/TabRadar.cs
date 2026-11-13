using System.Windows.Media;

namespace Eteris.Browser.Tabs;

// Âmbitos do radar de abas, pela ordem do seletor «Mostrar».
public enum RadarScope { All, Pinned, Audible, Sleeping, Duplicates, Closed, Done, Snoozed, Archived, Spaces }

// Ordenações do radar, pela ordem do seletor «Ordenar». Strip = ordem da faixa de abas (sem reordenar).
public enum RadarSort { Recent, Site, Strip }

// Uma linha da lista do radar: aba aberta (Tab) ou entrada das fechadas recentemente (Closed).
public sealed record RadarRow(BrowserTab? Tab, ClosedTab? Closed, string Title, string Address,
    string Subtitle, string Badge, Brush BadgeBrush, ImageSource? Image, string Status, string? Note = null, Data.DoneTab? Done = null,
    Data.SnoozedTab? Snoozed = null, Data.ArchivedTab? Archived = null, Workspace? Space = null)
{
    public object Identity => (object?)Tab ?? (object?)Closed ?? (object?)Done ?? (object?)Snoozed ?? Archived!;
}

// Lógica do radar de abas sem interface: filtros, duplicadas, ordenações e texto de estado.
// O UserControl (TabRadarView) só liga estas funções aos controlos.
public static class TabRadar
{
    // Os âmbitos do seletor «Mostrar», pela ordem. A janela privada não mostra os que vivem em disco (Feitas hoje,
    // Adiadas, Arquivo), mas tem Espaços como qualquer janela: «Noutros Espaços» fica.
    public static IReadOnlyList<RadarScope> ScopesFor(bool privateWindow) => privateWindow
        ? [RadarScope.All, RadarScope.Pinned, RadarScope.Audible, RadarScope.Sleeping, RadarScope.Duplicates, RadarScope.Closed, RadarScope.Spaces]
        : Enum.GetValues<RadarScope>();

    public static string ScopeLabel(RadarScope scope) => scope switch
    {
        RadarScope.All => "Todas as abas",
        RadarScope.Pinned => "Fixadas",
        RadarScope.Audible => "Com som",
        RadarScope.Sleeping => "A dormir",
        RadarScope.Duplicates => "Duplicadas",
        RadarScope.Closed => "Fechadas recentemente",
        RadarScope.Done => "Feitas hoje",
        RadarScope.Snoozed => "Adiadas",
        RadarScope.Archived => "Arquivo",
        _ => "Noutros Espaços",
    };


    public static IReadOnlyList<RadarRow> Rows(IReadOnlyList<BrowserTab> tabs, BrowserTab? active,
        IReadOnlyList<ClosedTab> closed, RadarScope scope, RadarSort sort, string query, IReadOnlyList<Data.DoneTab>? done = null,
        IReadOnlyList<Data.SnoozedTab>? snoozed = null, DateTime? nowLocal = null, IReadOnlyList<Data.ArchivedTab>? archived = null,
        IReadOnlyList<(Workspace Space, BrowserTab Tab)>? spaces = null)
    {
        query = query.Trim();
        var duplicates = Duplicates(tabs);
        var now = nowLocal ?? DateTime.Now;
        var nowUtc = nowLocal?.ToUniversalTime() ?? DateTime.UtcNow;

        var rows = scope == RadarScope.Closed
            ? closed.Select(FromClosed)
            : scope == RadarScope.Done
            ? (done ?? []).Select(FromDone)
            : scope == RadarScope.Snoozed
            ? (snoozed ?? []).Select(s => FromSnoozed(s, now))
            : scope == RadarScope.Archived
            ? (archived ?? []).Select(a => FromArchived(a, nowUtc))
            : scope == RadarScope.Spaces
            ? (spaces ?? []).Select(pair => FromSpace(pair.Tab, pair.Space))
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
        else if (sort == RadarSort.Recent && scope is not (RadarScope.Closed or RadarScope.Done or RadarScope.Snoozed or RadarScope.Archived))
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
            0 when scope == RadarScope.Snoozed => "Nenhuma aba adiada. «Adiar até…» está no menu da aba.",
            0 when scope == RadarScope.Archived => "O Arquivo está vazio: abas sem visita há dias vêm para aqui (Definições › Abas).",
            0 when scope == RadarScope.Spaces => "Nenhuma aba noutros Espaços. «Novo Espaço» está na paleta (Ctrl+K).",
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
        if (tab.IsPinned) status.Add(tab.IsAwayFromHome ? "Fixada · fora de casa" : "Fixada");
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

    // Aba de outro Espaço (a dormir na reserva): o Radar diz onde está e «Ir» muda de Espaço e ativa-a.
    public static RadarRow FromSpace(BrowserTab tab, Workspace space)
    {
        var status = new List<string> { $"No Espaço «{space.Name}»" };
        if (tab.IsPinned) status.Add(tab.IsAwayFromHome ? "Fixada · fora de casa" : "Fixada");
        if (tab.IsLocked) status.Add("Trancada");
        if (tab.IsSleeping) status.Add("A dormir");
        var site = Site(tab.Address);
        var subtitle = $"{site} · {(space.Icon is { } icon ? icon + " " : "")}{space.Name}";
        return new(tab, null, tab.CustomTitle ?? (string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title), tab.Address,
            subtitle, tab.Monogram ?? "É", tab.MonogramBrush, tab.Favicon, string.Join(" · ", status), tab.Note, Space: space);
    }

    public static RadarRow FromDone(Data.DoneTab tab)
    {
        var site = Site(tab.Url);
        var when = tab.DoneUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return new(null, null, string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title, tab.Url,
            site, SiteBadge.Letter(tab.Url) ?? "É", Brushes.SeaGreen, null, $"Feita às {when}", null, tab);
    }

    // Adiada: mostra o nome próprio (se o tem) e quando volta; a nota viaja com ela.
    public static RadarRow FromSnoozed(Data.SnoozedTab tab, DateTime nowLocal)
    {
        var site = Site(tab.Url);
        var title = tab.Name ?? (string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title);
        return new(null, null, title, tab.Url, site, SiteBadge.Letter(tab.Url) ?? "É", Brushes.DarkOrange, null,
            "Volta " + Snooze.Describe(tab.DueUtc.ToLocalTime(), nowLocal), tab.Note, null, tab);
    }

    // Arquivada: quando foi arquivada e desde quando não era visitada; «Repor» traz de volta.
    public static RadarRow FromArchived(Data.ArchivedTab tab, DateTime nowUtc)
    {
        var site = Site(tab.Url);
        var title = tab.Name ?? (string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title);
        var status = $"Arquivada {ArchivePolicy.Ago(tab.ArchivedUtc, nowUtc)} · última visita {ArchivePolicy.Ago(tab.LastActiveUtc, nowUtc)}";
        return new(null, null, title, tab.Url, site, SiteBadge.Letter(tab.Url) ?? "É", Brushes.SlateBlue, null, status, null, null, null, tab);
    }

    public static RadarRow FromClosed(ClosedTab tab)
    {
        var site = Site(tab.Address);
        return new(null, tab, string.IsNullOrWhiteSpace(tab.Title) ? site : tab.Title, tab.Address,
            site, SiteBadge.Letter(tab.Address) ?? "É", Brushes.SlateGray, null, "Fechada recentemente");
    }
}
