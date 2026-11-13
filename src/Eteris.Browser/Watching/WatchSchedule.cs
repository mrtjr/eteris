namespace Eteris.Browser.Watching;

// Quando uma vigia volta a visitar a página: o intervalo escolhido (nunca menos de 30 min), a dobrar em cada falha
// seguida até um dia, com ±10 % de variação para as visitas não caírem sempre à mesma hora. As vigias bloqueadas ou sem
// pedaço não voltam a ser visitadas (decisão 8): só a leitura passiva as lê.
public static class WatchSchedule
{
    public const int MinIntervalMinutes = 30;
    public const int DefaultIntervalMinutes = 120;

    private const double MaxMinutes = 24 * 60;
    private const double Jitter = 0.10;

    public static DateTime Next(DateTime nowUtc, int intervalMinutes, int failures, WatchStatus status, Random rng)
    {
        if (status is WatchStatus.Blocked or WatchStatus.Missing) return DateTime.MaxValue;

        // O expoente fica limitado: 2^16 já passa de um dia para qualquer intervalo.
        var minutes = Math.Max(intervalMinutes, MinIntervalMinutes) * Math.Pow(2, Math.Clamp(failures, 0, 16));
        minutes = Math.Min(minutes, MaxMinutes);
        minutes *= 1 - Jitter + rng.NextDouble() * 2 * Jitter;
        return nowUtc.AddMinutes(Math.Min(minutes, MaxMinutes));
    }
}
