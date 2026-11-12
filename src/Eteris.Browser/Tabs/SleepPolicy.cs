namespace Eteris.Browser.Tabs;

public static class SleepPolicy
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(20);

    // Uma aba adormece (liberta o processo do Chromium) se estiver em segundo plano sem uso há algum tempo,
    // nunca enquanto carrega ou toca áudio.
    // threshold zero = nunca adormecer. Uma aba trancada nunca adormece: foi protegida de propósito.
    // Nem com a câmara, o microfone ou o ecrã em uso (isCapturing), com multimédia a tocar mesmo sem som (isPlaying:
    // um vídeo em Picture-in-Picture silenciado) ou quando a página pediu há pouco para ficar acordada (isHeld: texto
    // por enviar, Picture-in-Picture; SleepProbe).
    public static bool ShouldSleep(bool isActive, bool isSleeping, bool isLoading, bool isAudible, DateTime lastActiveUtc, DateTime nowUtc,
        TimeSpan? threshold = null, bool isLocked = false, bool isCapturing = false, bool isPlaying = false, bool isHeld = false)
    {
        var limit = threshold ?? Threshold;
        return limit > TimeSpan.Zero && !isActive && !isSleeping && !isLoading && !isAudible && !isLocked && !isCapturing && !isPlaying
               && !isHeld && nowUtc - lastActiveUtc > limit;
    }

    // As rondas do sono são de minuto a minuto: um intervalo muito maior entre duas é o computador que esteve
    // suspenso (ou a hibernar), e esse tempo não conta para o prazo — no regresso, o «sem uso há» recomeça daí.
    public static readonly TimeSpan RoundGap = TimeSpan.FromMinutes(3);

    public static bool WasSuspended(DateTime? previousRoundUtc, DateTime nowUtc)
        => previousRoundUtc is { } previous && nowUtc - previous > RoundGap;

    // O último uso que conta: o real, ou o regresso de uma suspensão (floorUtc), o que for mais tarde.
    public static DateTime Later(DateTime lastActiveUtc, DateTime? floorUtc)
        => floorUtc is { } floor && floor > lastActiveUtc ? floor : lastActiveUtc;
}
