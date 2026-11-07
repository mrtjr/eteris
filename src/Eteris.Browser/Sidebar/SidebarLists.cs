using System.Globalization;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Sidebar;

// Uma linha das listas da barra lateral (Para ler, Favoritos, Histórico): título, site, uma linha de detalhe e o
// monograma do site (letra e cor, como nas abas sem ícone). Muted = já lido / sem destaque. Id = visita do
// histórico (para apagar só essa); Icon é posto pela janela quando o site tem ícone guardado.
public sealed record SidebarLink(string Url, string Title, string Host, string Detail, bool Muted, long Id = 0)
{
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

    // «Para ler»: por ler primeiro (ordem do repositório), com «há 2 dias» ou «lido» no detalhe.
    public static IReadOnlyList<SidebarLink> Reading(IEnumerable<ReadingItem> items, DateTime nowUtc)
        => items.Select(item => new SidebarLink(item.Url, TitleOf(item.Title, item.Url), HostOf(item.Url),
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
            rows.Add(new SidebarLink(visit.Url, TitleOf(visit.Title, visit.Url), HostOf(visit.Url), local.ToString("HH:mm", CultureInfo.InvariantCulture), false, visit.Id));
        }
        return rows;
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
