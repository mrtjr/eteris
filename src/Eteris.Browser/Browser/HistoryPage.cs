using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Browser;

// eteris://history — histórico agrupado por dia, com pesquisa e apagamento.
// As ações destrutivas levam um token aleatório desta sessão: nenhuma outra página o conhece.
public static class HistoryPage
{
    public const string Url = "eteris://history/";
    public const int MaxVisits = 3000;

    public static readonly string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-PT");

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(HistoryPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.history.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static bool IsHistory(string url) => url.StartsWith("eteris://history", StringComparison.OrdinalIgnoreCase);

    public static string DayLabel(DateTime day, DateTime now)
    {
        var days = (now.Date - day.Date).Days;
        if (days == 0) return "Hoje";
        if (days == 1) return "Ontem";
        var format = day.Year == now.Year ? "dddd, d 'de' MMMM" : "dddd, d 'de' MMMM 'de' yyyy";
        return day.ToString(format, Pt);
    }

    public static DateTime? RangeStart(string range, DateTime now) => range switch
    {
        "hour" => now.AddHours(-1),
        "today" => now.Date,
        "all" => DateTime.MinValue,
        _ => null,
    };

    public static bool IsAuthorized(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Query(uri, "t") is { } token
           && CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(Token));

    public static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == name) return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }

    public static string Render(IReadOnlyList<Visit> visits, DateTime now)
    {
        var groups = visits
            .GroupBy(v => v.VisitedUtc.ToLocalTime().Date)
            .Select(g => new
            {
                label = DayLabel(g.Key, now),
                items = g.Select(v => new
                {
                    id = v.Id,
                    url = v.Url,
                    title = v.Title.Length > 0 ? v.Title : v.Url,
                    host = Uri.TryCreate(v.Url, UriKind.Absolute, out var u) ? u.Host : v.Url,
                    time = v.VisitedUtc.ToLocalTime().ToString("HH:mm", Pt),
                    letter = SiteBadge.Letter(v.Url) ?? "?",
                    color = SiteBadge.Palette[SiteBadge.ColorIndex(v.Url)],
                }),
            });
        var data = new { token = Token, total = visits.Count, groups };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
