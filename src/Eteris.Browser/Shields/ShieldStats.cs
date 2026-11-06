namespace Eteris.Browser.Shields;

public static class ShieldStats
{
    private static int _sessionBlocked;
    private static int _pending;

    public static int SessionBlocked => Volatile.Read(ref _sessionBlocked);

    // Janelas privadas contam para a sessão mas nunca ficam no disco.
    public static void CountBlocked(bool persist = true)
    {
        Interlocked.Increment(ref _sessionBlocked);
        if (persist) Interlocked.Increment(ref _pending);
    }

    // Devolve (e zera) o que ainda não foi gravado no painel semanal.
    public static int TakePending() => Interlocked.Exchange(ref _pending, 0);
}
