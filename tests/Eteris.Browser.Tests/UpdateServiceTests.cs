using System.IO;
using System.Net.Http;
using Eteris.Browser.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Eteris.Browser.Tests;

internal sealed class ThrowingSource : IUpdateSource
{
    public int Feeds;

    public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        Interlocked.Increment(ref Feeds);
        throw new HttpRequestException("sem rede");
    }

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
        => throw new HttpRequestException("sem rede");
}

internal sealed class FeedSource(string json) : IUpdateSource
{
    public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        => Task.FromResult(VelopackAssetFeed.FromJson(json));

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
        => throw new NotSupportedException();
}

// Uma instalação Velopack de teste (EterisBrowser 0.1.0, pasta de pacotes temporária): sem ela o UpdateManager diz «não
// instalado» e nunca chega a chamar a fonte.
internal static class InstalledLocator
{
    public static TestVelopackLocator Create()
    {
        var packagesDir = Path.Combine(Path.GetTempPath(), "eteris-test-" + Guid.NewGuid());
        Directory.CreateDirectory(packagesDir);
        return new TestVelopackLocator("EterisBrowser", "0.1.0", packagesDir, null);
    }
}

public class UpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_WhenSourceThrows_ReturnsNull()
    {
        var source = new ThrowingSource();

        Assert.Null(await UpdateService.CheckAsync(source, InstalledLocator.Create()));
        Assert.True(source.Feeds >= 1, "a fonte nunca foi chamada: o teste não exercita o catch");
    }

    [Fact]
    public async Task CheckAsync_WhenNotInstalled_ReturnsNull()
        => Assert.Null(await UpdateService.CheckAsync(new GithubSource(UpdateService.RepoUrl, null, false)));

    [Fact]
    public async Task CheckAsync_WhenInstalledAndNewerVersionAvailable_ReturnsUpdate()
    {
        var locator = InstalledLocator.Create();
        const string feed = """
            {"Assets":[{"PackageId":"EterisBrowser","Version":"0.1.1","Type":"Full",
            "FileName":"EterisBrowser-0.1.1-full.nupkg","SHA1":"0000000000000000000000000000000000000000","Size":1}]}
            """;

        var update = await UpdateService.CheckAsync(new FeedSource(feed), locator);

        Assert.NotNull(update);
        Assert.Equal("0.1.1", update.TargetFullRelease.Version.ToString());
    }
}

// Lê de volta o estado estático UpdateService.State; nenhum teste o leva a Ready (não volta atrás).
[Collection(GlobalStateCollection.Name)]
public class UpdateStateTests
{
    [Fact]
    public async Task NotInstalled_EndsInDevelopmentState()
    {
        await UpdateService.CheckAndDownloadAsync();
        Assert.Equal(UpdateState.Development, UpdateService.State);
    }

    [Fact]
    public async Task CheckAndDownload_WhenTheSourceThrows_EndsInFailed()
    {
        var source = new ThrowingSource();

        await UpdateService.CheckAndDownloadAsync(() => new UpdateManager(source, null, InstalledLocator.Create()));

        Assert.Equal(UpdateState.Failed, UpdateService.State);
        Assert.True(source.Feeds >= 1, "a fonte nunca foi chamada: o estado Failed veio de outra razão");
    }

    [Fact]
    public async Task CheckAndDownload_WithNoNewerVersion_EndsUpToDate()
    {
        const string feed = """
            {"Assets":[{"PackageId":"EterisBrowser","Version":"0.1.0","Type":"Full",
            "FileName":"EterisBrowser-0.1.0-full.nupkg","SHA1":"0000000000000000000000000000000000000000","Size":1}]}
            """;

        await UpdateService.CheckAndDownloadAsync(() => new UpdateManager(new FeedSource(feed), null, InstalledLocator.Create()));

        Assert.Equal(UpdateState.UpToDate, UpdateService.State);
    }
}
