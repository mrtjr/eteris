namespace Eteris.Browser.Tabs;

// O temporizador opcional da Pausa (MainWindow.Pause.cs): «Sem fim · 1 min · 5 min · 15 min», o último escolhido lembrado
// (UserSettings.PauseMinutes). Só conta: o fim nunca acaba a Pausa nem toca som — diz «Pronto. Volte quando quiser.».
// A contagem muda de minuto a minuto (é uma região viva educada: o leitor de ecrã não a ouve a cada segundo).
public static class PauseTimer
{
    // 0 = sem fim.
    public static readonly int[] Choices = [0, 1, 5, 15];

    public const string Done = "Pronto. Volte quando quiser.";

    public static bool IsChoice(int minutes) => Choices.Contains(minutes);

    public static string Label(int minutes) => minutes == 0 ? "Sem fim" : $"{minutes} min";

    // null: sem fim (nenhuma contagem).
    public static string? Remaining(TimeSpan? left)
    {
        if (left is not { } time) return null;
        if (time <= TimeSpan.Zero) return Done;
        if (time < TimeSpan.FromMinutes(1)) return "falta menos de 1 min";
        var minutes = (int)Math.Ceiling(time.TotalMinutes);
        return minutes == 1 ? "falta 1 min" : $"faltam {minutes} min";
    }

    public static bool Ended(TimeSpan? left) => left is { } time && time <= TimeSpan.Zero;

    // Daqui a quanto tempo o texto muda (no máximo um minuto); null sem contagem ou já no fim.
    public static TimeSpan? NextUpdate(TimeSpan? left)
    {
        if (left is not { } time || time <= TimeSpan.Zero) return null;
        if (time < TimeSpan.FromMinutes(1)) return time;
        // O texto é o minuto por cima: muda quando o tempo que falta desce ao minuto inteiro de baixo (5:00 → 4:00).
        // Com 1:00 certo («falta 1 min»), o texto muda logo a seguir («falta menos de 1 min»).
        var whole = TimeSpan.FromMinutes(Math.Ceiling(time.TotalMinutes) - 1);
        return whole > TimeSpan.Zero ? time - whole : time - TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1);
    }
}
