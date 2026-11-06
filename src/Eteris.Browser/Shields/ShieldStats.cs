namespace Eteris.Browser.Shields;

public static class ShieldStats
{
    private static int _sessionBlocked;

    public static int SessionBlocked => Volatile.Read(ref _sessionBlocked);

    public static void CountBlocked() => Interlocked.Increment(ref _sessionBlocked);
}
