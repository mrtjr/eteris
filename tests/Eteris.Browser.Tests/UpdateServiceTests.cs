using System.IO;
using System.Net.Http;
using Eteris.Browser.Updates;
using Velopack;
using Velopack.Locators;
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

    private sealed class FeedSource(string json) : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => Task.FromResult(VelopackAssetFeed.FromJson(json));

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task CheckAsync_WhenSourceThrows_ReturnsNull()
        => Assert.Null(await UpdateService.CheckAsync(new ThrowingSource()));

    [Fact]
    public async Task CheckAsync_WhenNotInstalled_ReturnsNull()
        => Assert.Null(await UpdateService.CheckAsync(new GithubSource(UpdateService.RepoUrl, null, false)));

    [Fact]
    public async Task CheckAsync_WhenInstalledAndNewerVersionAvailable_ReturnsUpdate()
    {
        var packagesDir = Path.Combine(Path.GetTempPath(), "eteris-test-" + Guid.NewGuid());
        Directory.CreateDirectory(packagesDir);
        var locator = new TestVelopackLocator("EterisBrowser", "0.1.0", packagesDir, null);
        const string feed = """
            {"Assets":[{"PackageId":"EterisBrowser","Version":"0.1.1","Type":"Full",
            "FileName":"EterisBrowser-0.1.1-full.nupkg","SHA1":"0000000000000000000000000000000000000000","Size":1}]}
            """;

        var update = await UpdateService.CheckAsync(new FeedSource(feed), locator);

        Assert.NotNull(update);
        Assert.Equal("0.1.1", update.TargetFullRelease.Version.ToString());
    }
}
