using Velopack;
using Velopack.Sources;

namespace Eteris.Browser.Updates;

public static class UpdateService
{
    public const string RepoUrl = "https://github.com/mrtjr/eteris";

    public static UpdateInfo? Pending { get; private set; }

    public static async Task<UpdateInfo?> CheckAsync(IUpdateSource source)
    {
        try
        {
            var manager = new UpdateManager(source);
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
        var source = new GithubSource(RepoUrl, null, false);
        var update = await CheckAsync(source);
        if (update is null) return;
        try
        {
            await new UpdateManager(source).DownloadUpdatesAsync(update);
            Pending = update;
        }
        catch
        {
            // sem rede ou download interrompido: tenta no próximo arranque
        }
    }
}
