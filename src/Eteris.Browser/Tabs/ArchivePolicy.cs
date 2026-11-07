namespace Eteris.Browser.Tabs;

// Arquivo automático: uma aba sem visita há N dias (definição; 0 = nunca) desvanece-se a partir de metade do prazo
// e, no fim, sai da faixa para o «Arquivo» do Radar, de onde se repõe. Irmã da SleepPolicy: só decide, não age.
public static class ArchivePolicy
{
    public static readonly int[] Choices = [0, 3, 7, 14, 30];
    public const int DefaultDays = 7;

    // Opacidade mínima de uma aba quase a arquivar (continua legível).
    public const double FadedOpacity = 0.45;

    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    // Nunca se arquiva o que o utilizador marcou de propósito, o que está à vista ou a tocar, nem uma aba nova.
    public static bool IsExempt(bool onScreen, bool isPinned, bool isLocked, bool hasNote, bool isAudible, bool isNewTab)
        => onScreen || isPinned || isLocked || hasNote || isAudible || isNewTab;

    public static bool ShouldArchive(bool onScreen, bool isPinned, bool isLocked, bool hasNote, bool isAudible, bool isNewTab,
        DateTime lastActiveUtc, DateTime nowUtc, int days)
        => days > 0 && !IsExempt(onScreen, isPinned, isLocked, hasNote, isAudible, isNewTab) && nowUtc - lastActiveUtc >= TimeSpan.FromDays(days);

    // 1 até metade do prazo; depois desce em linha reta até FadedOpacity no fim. Isentas e prazo desligado: 1.
    public static double Opacity(bool exempt, DateTime lastActiveUtc, DateTime nowUtc, int days)
    {
        if (exempt || days <= 0) return 1;
        var total = TimeSpan.FromDays(days).TotalSeconds;
        var elapsed = Math.Clamp((nowUtc - lastActiveUtc).TotalSeconds, 0, total);
        var half = total / 2;
        if (elapsed <= half) return 1;
        var progress = (elapsed - half) / half;
        return Math.Round(1 - (1 - FadedOpacity) * progress, 3);
    }

    // «há 3 dias», «ontem», «hoje» — para o Radar.
    public static string Ago(DateTime whenUtc, DateTime nowUtc)
    {
        var days = (int)Math.Floor((nowUtc - whenUtc).TotalDays);
        return days <= 0 ? "hoje" : days == 1 ? "ontem" : $"há {days} dias";
    }
}
