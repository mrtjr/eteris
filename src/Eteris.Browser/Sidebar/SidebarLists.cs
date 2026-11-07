using System.ComponentModel;
using System.Globalization;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Sidebar;

// Uma linha das listas da barra lateral (Para ler, Favoritos, Histórico): título, site, uma linha de detalhe e o
// monograma do site (letra e cor, como nas abas sem ícone). Muted = já lido / sem destaque. Id = visita do
// histórico (a mais recente da linha); VisitedUtc = quando foi (o dia da linha, para apagar as visitas desse dia);
// Icon é posto pela janela quando o site tem ícone guardado; IsCurrent = a linha escolhida com as setas.
public sealed record SidebarLink(string Url, string Title, string Host, string Detail, bool Muted, long Id = 0, DateTime VisitedUtc = default)
    : INotifyPropertyChanged
{
    private bool _isCurrent;

    // Cada linha é uma linha (identidade): o estado que muda (IsCurrent, Icon, quem ouve) não mexe na igualdade
    // que a lista do WPF usa para encontrar o contentor do item.
    public bool Equals(SidebarLink? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public string Letter => SiteBadge.Letter(Url) ?? "·";

    public string Color => SiteBadge.Palette[SiteBadge.ColorIndex(Url)];

    public string Subline => Detail.Length > 0 ? $"{Host} · {Detail}" : Host;

    public System.Windows.Media.ImageSource? Icon { get; set; }
}

// Cabeçalho de um grupo (o histórico agrupa por dia).
public sealed record SidebarHeading(string Title);

public static class SidebarLists
{
    private static readonly CultureInfo Portuguese = new("pt-PT");

    // «Para ler»: por ler primeiro (ordem do repositório), com «há 2 dias» ou «lido» no detalhe, filtrados pelas
    // palavras escritas.
    public static IReadOnlyList<SidebarLink> Reading(IEnumerable<ReadingItem> items, DateTime nowUtc, string query = "")
        => items.Where(item => Matches(item.Title, item.Url, query)).Select(item => new SidebarLink(item.Url, TitleOf(item.Title, item.Url), HostOf(item.Url),
                item.Read ? "Lido" : Ago(item.AddedUtc, nowUtc), item.Read))
            .ToList();

    // «Favoritos»: pela ordem do repositório (mais recentes primeiro), filtrados pelas palavras escritas.
    public static IReadOnlyList<SidebarLink> Bookmarks(IEnumerable<Bookmark> marks, string query)
        => marks.Where(mark => Matches(mark.Title, mark.Url, query))
            .Select(mark => new SidebarLink(mark.Url, TitleOf(mark.Title, mark.Url), HostOf(mark.Url), string.Empty, false))
            .ToList();

    // «Histórico»: as visitas (mais recentes primeiro) em grupos por dia local — «Hoje», «Ontem», o dia da semana
    // na última semana, depois a data — com a hora no detalhe; a mesma página repetida no mesmo dia aparece uma vez.
    public static IReadOnlyList<object> History(IEnumerable<Visit> visits, DateTime nowLocal, string query, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var rows = new List<object>();
        DateOnly? currentDay = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var visit in visits)
        {
            if (!Matches(visit.Title, visit.Url, query)) continue;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(visit.VisitedUtc, DateTimeKind.Utc), zone);
            var day = DateOnly.FromDateTime(local);
            if (day != currentDay)
            {
                currentDay = day;
                seen.Clear();
                rows.Add(new SidebarHeading(DayLabel(day, DateOnly.FromDateTime(nowLocal))));
            }
            if (!seen.Add(visit.Url)) continue;
            rows.Add(new SidebarLink(visit.Url, TitleOf(visit.Title, visit.Url), HostOf(visit.Url), local.ToString("HH:mm", CultureInfo.InvariantCulture), false, visit.Id,
                DateTime.SpecifyKind(visit.VisitedUtc, DateTimeKind.Utc)));
        }
        return rows;
    }

    // O dia local da visita, em UTC [início, fim): o intervalo que a linha do histórico representa.
    public static (DateTime FromUtc, DateTime ToUtc) DayRangeUtc(DateTime visitedUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(visitedUtc, DateTimeKind.Utc), zone);
        return (StartOfDayUtc(local.Date, zone), StartOfDayUtc(local.Date.AddDays(1), zone));
    }

    // Meia-noite local em UTC; onde a hora muda à meia-noite (a meia-noite não existe), a primeira hora que existe.
    private static DateTime StartOfDayUtc(DateTime day, TimeZoneInfo zone)
    {
        var start = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(start)) start = start.AddMinutes(15);
        return zone.IsAmbiguousTime(start)
            ? DateTime.SpecifyKind(start - zone.GetAmbiguousTimeOffsets(start).Max(), DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(start, zone);
    }

    // Setas na lista (a partir do filtro): a linha seguinte/anterior que é um link (os cabeçalhos dos dias saltam-se);
    // sem nenhuma escolhida, a primeira; nas pontas fica onde está. -1 quando não há links.
    public static int MoveCursor(IReadOnlyList<object> rows, int current, int delta)
    {
        if (current < 0 || current >= rows.Count || rows[current] is not SidebarLink)
        {
            for (var i = 0; i < rows.Count; i++)
                if (rows[i] is SidebarLink) return i;
            return -1;
        }
        var step = Math.Sign(delta);
        for (var i = current + step; step != 0 && i >= 0 && i < rows.Count; i += step)
            if (rows[i] is SidebarLink) return i;
        return current;
    }

    public static string DayLabel(DateOnly day, DateOnly today)
    {
        var days = today.DayNumber - day.DayNumber;
        if (days == 0) return "Hoje";
        if (days == 1) return "Ontem";
        if (days is > 1 and < 7) return Capitalize(day.ToString("dddd", Portuguese));
        return Capitalize(day.ToString(day.Year == today.Year ? "d 'de' MMMM" : "d 'de' MMMM 'de' yyyy", Portuguese));
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Portuguese) + text[1..];

    // As mesmas regras do filtro das abas: todas as palavras, sem olhar a acentos; vazio deixa passar tudo.
    public static bool Matches(string? title, string url, string query)
        => query.Trim().Length == 0 || TabFilter.Matches(title ?? string.Empty, url, query);

    // Título legível: o da página, senão o endereço sem esquema (uma linha só).
    public static string TitleOf(string? title, string url)
    {
        var clean = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 0) return clean;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.PathAndQuery.TrimEnd('/') : url;
    }

    public static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return url;
        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
    }

    // «agora mesmo», «há 5 min», «há 3 h», «ontem», «há 4 dias», e a partir de um mês a data.
    public static string Ago(DateTime whenUtc, DateTime nowUtc)
    {
        var elapsed = nowUtc - whenUtc;
        if (elapsed < TimeSpan.FromMinutes(1)) return "agora mesmo";
        if (elapsed < TimeSpan.FromHours(1)) return $"há {(int)elapsed.TotalMinutes} min";
        if (elapsed < TimeSpan.FromHours(24)) return $"há {(int)elapsed.TotalHours} h";
        var days = (int)Math.Floor(elapsed.TotalDays);
        if (days == 1) return "ontem";
        if (days < 30) return $"há {days} dias";
        return whenUtc.ToLocalTime().ToString("d MMM yyyy", Portuguese);
    }
}
