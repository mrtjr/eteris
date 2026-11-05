using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Eteris.Browser.Updates;

public static class UpdateService
{
    public const string RepoUrl = "https://github.com/mrtjr/eteris";

    private static UpdateManager? _manager;

    public static UpdateInfo? Pending { get; private set; }

    public static async Task<UpdateInfo?> CheckAsync(IUpdateSource source, IVelopackLocator? locator = null)
    {
        try
        {
            var manager = new UpdateManager(source, null, locator);
            if (!manager.IsInstalled) return null;
            return await manager.CheckForUpdatesAsync();
        }
        catch
        {
            return null;
        }
    }

    public static async Task CheckAndDownloadAsync()
    {
        var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
        try
        {
            if (!manager.IsInstalled) return;
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return;
            await manager.DownloadUpdatesAsync(update);
            _manager = manager;
            Pending = update;
        }
        catch
        {
            // sem rede ou download interrompido: tenta no próximo arranque
        }
    }

    // O Velopack não aplica pacotes descarregados sozinho: Update.exe espera que o processo saia e troca a pasta current\.
    public static void ApplyPendingOnExit()
    {
        if (Pending is null || _manager is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(Pending.TargetFullRelease, silent: true, restart: false);
        }
        catch
        {
            // fica para o próximo arranque
        }
    }
}
