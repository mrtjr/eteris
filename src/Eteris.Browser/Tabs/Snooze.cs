using System.Globalization;

namespace Eteris.Browser.Tabs;

// Uma escolha do menu «Adiar até…»: rótulo, hora legível e o instante (hora local) em que a aba volta.
public sealed record SnoozeOption(string Label, string When, DateTime DueLocal);

// «Adiar até…»: a aba sai da faixa e volta à hora escolhida. As opções dependem da hora a que se pede (não se
// oferece «hoje à noite» às 23 h) e os textos são os que o utilizador vê no menu, no aviso e no Radar.
public static class Snooze
{
    public const int MorningHour = 9;
    public const int EveningHour = 19;
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private static readonly CultureInfo Pt = new("pt-PT");

    public static IReadOnlyList<SnoozeOption> Options(DateTime nowLocal)
    {
        var options = new List<SnoozeOption>();
        var inAnHour = nowLocal.AddHours(1);
        options.Add(new("Daqui a 1 hora", inAnHour.ToString("HH:mm", Pt), inAnHour));

        // «Hoje à noite» só enquanto faltar pelo menos meia hora para as 19:00.
        var evening = nowLocal.Date.AddHours(EveningHour);
        if (evening - nowLocal >= TimeSpan.FromMinutes(30)) options.Add(new("Hoje à noite", $"{EveningHour:00}:00", evening));

        var tomorrow = nowLocal.Date.AddDays(1).AddHours(MorningHour);
        options.Add(new("Amanhã de manhã", $"{MorningHour:00}:00", tomorrow));

        // Segunda de manhã: a próxima segunda-feira; se for já amanhã, «Amanhã de manhã» chega, e numa segunda-feira
        // é a mesma hora que «Daqui a uma semana».
        var nextWeek = nowLocal.Date.AddDays(7).AddHours(MorningHour);
        var daysToMonday = ((int)DayOfWeek.Monday - (int)nowLocal.DayOfWeek + 7) % 7;
        if (daysToMonday == 0) daysToMonday = 7;
        var monday = nowLocal.Date.AddDays(daysToMonday).AddHours(MorningHour);
        if (monday != tomorrow && monday != nextWeek) options.Add(new("Segunda de manhã", $"{MorningHour:00}:00", monday));


        options.Add(new("Daqui a uma semana", Describe(nextWeek, nowLocal), nextWeek));
        return options;
    }

    // Quais já venceram.
    public static IReadOnlyList<T> Due<T>(IEnumerable<T> snoozed, Func<T, DateTime> dueUtc, DateTime nowUtc)
        => snoozed.Where(s => dueUtc(s) <= nowUtc).ToList();

    // «hoje às 19:00», «amanhã às 09:00», «segunda às 09:00», «14 nov às 09:00».
    public static string Describe(DateTime dueLocal, DateTime nowLocal)
    {
        var time = dueLocal.ToString("HH:mm", Pt);
        var days = (dueLocal.Date - nowLocal.Date).Days;
        if (days == 0) return $"hoje às {time}";
        if (days == 1) return $"amanhã às {time}";
        if (days is > 1 and < 7) return $"{Pt.DateTimeFormat.GetDayName(dueLocal.DayOfWeek).Replace("-feira", "")} às {time}";
        return $"{dueLocal.ToString("d MMM", Pt).Replace(".", "")} às {time}";
    }
}

// Adiadas que já voltaram à faixa mas cuja linha não se conseguiu apagar (base de dados só de leitura, disco cheio):
// lembradas em memória para não voltarem outra vez a cada minuto nem continuarem na lista «Adiadas».
public sealed class WokenSnoozes
{
    private readonly HashSet<long> _ids = [];
    private readonly Lock _lock = new();

    public void Remember(long id)
    {
        lock (_lock) _ids.Add(id);
    }

    public IReadOnlyList<T> Pending<T>(IEnumerable<T> snoozed, Func<T, long> id)
    {
        lock (_lock) return snoozed.Where(s => !_ids.Contains(id(s))).ToList();
    }
}
