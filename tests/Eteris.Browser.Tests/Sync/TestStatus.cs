using Eteris.Sync.Engine;

namespace Eteris.Browser.Tests.Sync;

// O único sítio dos testes novos da 0.2.15 que chama o construtor posicional de SyncStatus: quando o registo mudar,
// acerta-se aqui.
internal static class TestStatus
{
    // Os restantes campos de SyncStatus acertam-se com «with { … }» no teste que precisar deles.
    public static SyncStatus Of(SyncPhase phase, bool running = false, long last = 0, string? error = null,
        IReadOnlyList<HostRequest>? requests = null, GuestPairing? pairing = null)
        => new(phase, running, last, error, null, pairing, requests ?? [], [], false, 0);
}
