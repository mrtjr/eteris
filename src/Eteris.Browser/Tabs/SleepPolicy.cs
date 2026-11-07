namespace Eteris.Browser.Tabs;

public static class SleepPolicy
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(20);

    // Uma aba adormece (liberta o processo do Chromium) se estiver em segundo plano sem uso há algum tempo,
    // nunca enquanto carrega ou toca áudio.
    // threshold zero = nunca adormecer. Uma aba trancada nunca adormece: foi protegida de propósito.
    public static bool ShouldSleep(bool isActive, bool isSleeping, bool isLoading, bool isAudible, DateTime lastActiveUtc, DateTime nowUtc,
        TimeSpan? threshold = null, bool isLocked = false)
    {
        var limit = threshold ?? Threshold;
        return limit > TimeSpan.Zero && !isActive && !isSleeping && !isLoading && !isAudible && !isLocked && nowUtc - lastActiveUtc > limit;
    }
}
