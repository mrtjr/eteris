using System.Globalization;

namespace Eteris.Browser.Shields;

// Estimativa (assumidamente aproximada) do que o escudo poupou: ~30 KB e ~50 ms por pedido bloqueado.
public static class PrivacyEstimate
{
    public const int BytesPerRequest = 30_000;
    public const double SecondsPerRequest = 0.05;

    private static readonly string[] Initials = ["D", "S", "T", "Q", "Q", "S", "S"];

    public static string Describe(int blocked)
    {
        if (blocked <= 0) return string.Empty;
        return $"≈ {Size((long)blocked * BytesPerRequest)} e {Duration(blocked * SecondsPerRequest)} poupados";
    }

    public static string[] DayInitials(DateTime today)
        => Enumerable.Range(0, 7).Select(i => Initials[(int)today.AddDays(i - 6).DayOfWeek]).ToArray();

    private static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{Math.Round(bytes / 1e9)} GB",
        >= 1_000_000 => $"{Math.Round(bytes / 1e6)} MB",
        _ => $"{Math.Round(bytes / 1e3)} KB",
    };

    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min";
        return $"{Math.Max(1, span.Seconds).ToString(CultureInfo.InvariantCulture)} s";
    }
}
