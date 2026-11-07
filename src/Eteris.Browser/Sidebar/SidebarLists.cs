using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Sidebar;

// Uma linha das listas da barra lateral (Para ler, Favoritos, Histórico): título, site, uma linha de detalhe e o
// monograma do site (letra e cor, como nas abas sem ícone). Muted = já lido / sem destaque.
public sealed record SidebarLink(string Url, string Title, string Host, string Detail, bool Muted)
{
    public string Letter => SiteBadge.Letter(Url) ?? "·";

    public string Color => SiteBadge.Palette[SiteBadge.ColorIndex(Url)];
}

public static class SidebarLists
{
    // «Para ler»: por ler primeiro (ordem do repositório), com «há 2 dias» ou «lido» no detalhe.
    public static IReadOnlyList<SidebarLink> Reading(IEnumerable<ReadingItem> items, DateTime nowUtc)
        => items.Select(item => new SidebarLink(item.Url, TitleOf(item.Title, item.Url), HostOf(item.Url),
                item.Read ? "Lido" : Ago(item.AddedUtc, nowUtc), item.Read))
            .ToList();

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
        return whenUtc.ToLocalTime().ToString("d MMM yyyy", new System.Globalization.CultureInfo("pt-PT"));
    }
}
