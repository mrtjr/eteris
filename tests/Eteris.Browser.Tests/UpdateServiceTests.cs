using System.Net.Http;
using Eteris.Browser.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Eteris.Browser.Tests;

public class UpdateServiceTests
{
    private sealed class ThrowingSource : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => throw new HttpRequestException("sem rede");

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
            => throw new HttpRequestException("sem rede");
    }

    [Fact]
    public async Task CheckAsync_WhenSourceThrows_ReturnsNull()
        => Assert.Null(await UpdateService.CheckAsync(new ThrowingSource()));

    [Fact]
    public async Task CheckAsync_WhenNotInstalled_ReturnsNull()
        => Assert.Null(await UpdateService.CheckAsync(new GithubSource(UpdateService.RepoUrl, null, false)));
}
