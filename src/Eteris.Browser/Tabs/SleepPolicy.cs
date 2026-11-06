namespace Eteris.Browser.Tabs;

public static class SleepPolicy
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(20);

    // Uma aba adormece (liberta o processo do Chromium) se estiver em segundo plano sem uso há algum tempo,
    // nunca enquanto carrega ou toca áudio.
    public static bool ShouldSleep(bool isActive, bool isSleeping, bool isLoading, bool isAudible, DateTime lastActiveUtc, DateTime nowUtc)
        => !isActive && !isSleeping && !isLoading && !isAudible && nowUtc - lastActiveUtc > Threshold;
}
