using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Eteris.Browser.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace Eteris.Browser.Tests;

// INF-02 e CAD-01 (auditoria 2): o Éteris só aceita pacotes que constem de um releases.win.json assinado (ECDSA P-256,
// SHA-256, assinatura r||s em Base64 no .sig) com uma chave pública embutida, e volta a conferir o SHA256 de cada pacote
// depois de o descarregar. As chaves aqui são de teste, geradas em memória.
public sealed class UpdateSignatureTests : IDisposable
{
    private static readonly string A = new('A', 64);
    private static readonly string B = new('B', 64);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eteris-sig-" + Guid.NewGuid().ToString("N"));

    public UpdateSignatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        _other.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Sign(ECDsa key, byte[] bytes)
        => Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string Pub(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static string Asset(string version, string? sha256, string fileVersion)
        => "{\"PackageId\":\"EterisBrowser\",\"Version\":\"" + version + "\",\"Type\":\"Full\",\"FileName\":\"EterisBrowser-" + fileVersion
           + "-full.nupkg\",\"SHA1\":\"0000000000000000000000000000000000000000\"" + (sha256 is null ? "" : ",\"SHA256\":\"" + sha256 + "\"")
           + ",\"Size\":1}";

    private static string Index(params (string Version, string Sha256)[] entries)
        => "{\"Assets\":[" + string.Join(",", entries.Select(e => Asset(e.Version, e.Sha256, e.Version))) + "]}";

    private sealed class FeedSource(string json, byte[]? download = null) : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => Task.FromResult(VelopackAssetFeed.FromJson(json));

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
            => File.WriteAllBytesAsync(localFile, download ?? throw new NotSupportedException(), cancelToken);
    }

    private sealed class QuietLogger : IVelopackLogger
    {
        public void Log(VelopackLogLevel level, string? message, Exception? exception) { }
    }

    // O índice assinado (e a assinatura) que o «servidor» devolve: o .sig para os nomes acabados em .sig.
    private static Func<string, CancellationToken, Task<byte[]>> Fetch(byte[] index, string? signature)
        => (name, _) => name.EndsWith(UpdateSignature.SignatureSuffix, StringComparison.Ordinal)
            ? signature is null ? throw new HttpRequestException("404") : Task.FromResult(Encoding.ASCII.GetBytes(signature))
            : Task.FromResult(index);

    private TestVelopackLocator Locator() => new("EterisBrowser", "0.1.0", _dir, null);

    private async Task<string?> CheckSigned(string signedIndex, string feed)
    {
        var bytes = Encoding.UTF8.GetBytes(signedIndex);
        var source = new SignedUpdateSource(new FeedSource(feed), Fetch(bytes, Sign(_key, bytes)), [Pub(_key)]);
        return (await UpdateService.CheckAsync(source, Locator()))?.TargetFullRelease.Version.ToString();
    }

    private static readonly byte[] Data = "abc"u8.ToArray();

    [Fact]
    public void Verify_AcceptsTheSignedBytes_AndRefusesOneChangedByte()
    {
        var sig = Sign(_key, Data);
        Assert.True(UpdateSignature.Verify(Data, sig, [Pub(_key)]));
        Assert.False(UpdateSignature.Verify("abd"u8, sig, [Pub(_key)]));
    }

    [Fact]
    public void Verify_RefusesAnotherKey_AnEmptyKeyList_AndGarbage()
    {
        var sig = Sign(_key, Data);
        Assert.False(UpdateSignature.Verify(Data, sig, [Pub(_other)]));
        Assert.False(UpdateSignature.Verify(Data, sig, []));
        Assert.False(UpdateSignature.Verify(Data, "isto nao e base64", [Pub(_key)]));
        Assert.False(UpdateSignature.Verify(Data, null, [Pub(_key)]));
        Assert.False(UpdateSignature.Verify(Data, sig, ["AAAA"]));
    }

    [Fact]
    public void Verify_AcceptsAnyOfTheListedKeys()
        => Assert.True(UpdateSignature.Verify(Data, Sign(_key, Data), [Pub(_other), Pub(_key)]));

    [Fact]
    public void ParseKeys_SkipsCommentsAndBlankLines()
        => Assert.Equal(["AAA=", "BBB="], UpdateSignature.ParseKeys("# chave 1\r\nAAA=\r\n\r\n  BBB=  \n# fim\n"));

    // Passa com zero chaves (o estado até o dono gerar as dele): cada linha que lá estiver tem de ser uma pública P-256.
    [Fact]
    public void EmbeddedKeys_AreP256PublicKeys()
        => Assert.All(UpdateSignature.EmbeddedKeys, k =>
        {
            using var e = ECDsa.Create();
            e.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _);
            Assert.Equal(256, e.KeySize);
        });

    [Fact]
    public async Task SignedSource_ValidIndex_ReturnsTheUpdate()
        => Assert.Equal("0.1.1", await CheckSigned(Index(("0.1.1", A)), Index(("0.1.1", A))));

    [Fact]
    public async Task SignedSource_IndexWithOneByteChanged_IsRefused()
    {
        var bytes = Encoding.UTF8.GetBytes(Index(("0.1.1", A)));
        var sig = Sign(_key, bytes);
        bytes[^3] ^= 1;
        var source = new SignedUpdateSource(new FeedSource(Index(("0.1.1", A))), Fetch(bytes, sig), [Pub(_key)]);
        Assert.Null(await UpdateService.CheckAsync(source, Locator()));
    }

    [Fact]
    public async Task SignedSource_WithoutSignatureFile_IsRefused()
    {
        var source = new SignedUpdateSource(new FeedSource(Index(("0.1.1", A))), Fetch(Encoding.UTF8.GetBytes(Index(("0.1.1", A))), null), [Pub(_key)]);
        Assert.Null(await UpdateService.CheckAsync(source, Locator()));
    }

    // Sem nenhuma chave embutida (update-keys.txt só com comentários), nada é aceite e nada se pede à rede: falha fechada.
    [Fact]
    public async Task SignedSource_WithoutKeys_IsRefusedWithoutFetching()
    {
        var fetched = 0;
        var source = new SignedUpdateSource(new FeedSource(Index(("0.1.1", A))), (_, _) => { fetched++; return Task.FromResult(Array.Empty<byte>()); }, []);
        Assert.Null(await UpdateService.CheckAsync(source, Locator()));
        await Assert.ThrowsAsync<UpdateSignatureException>(() => source.GetReleaseFeed(new QuietLogger(), "EterisBrowser", "win"));
        Assert.Equal(0, fetched);
    }

    [Fact]
    public async Task SignedSource_DropsAnAssetWhoseSha256IsNotInTheSignedIndex()
    {
        Assert.Equal("0.1.1", await CheckSigned(Index(("0.1.1", A)), Index(("0.1.1", A), ("0.1.2", B))));
        Assert.Null(await CheckSigned(Index(("0.1.1", A)), Index(("0.1.1", B))));
    }

    // Sem isto, um pacote antigo e genuíno podia ser anunciado como versão nova: os campos do feed não assinado não valem.
    [Fact]
    public async Task SignedSource_DropsAnAssetWhoseVersionIsNotTheSignedOne()
        => Assert.Null(await CheckSigned(Index(("0.1.1", A)), "{\"Assets\":[" + Asset("0.1.2", A, "0.1.1") + "]}"));

    [Fact]
    public async Task SignedSource_DropsAssetsWithOnlySha1()
        => Assert.Null(await CheckSigned("{\"Assets\":[" + Asset("0.1.1", null, "0.1.1") + "]}", Index(("0.1.1", A))));

    [Fact]
    public async Task Download_WithAnotherSha256_ThrowsAndDeletesTheFile()
    {
        var expected = Convert.ToHexString(SHA256.HashData("y"u8));
        var bytes = Encoding.UTF8.GetBytes(Index(("0.1.1", expected)));
        var source = new SignedUpdateSource(new FeedSource(Index(("0.1.1", expected)), "x"u8.ToArray()), Fetch(bytes, Sign(_key, bytes)), [Pub(_key)]);
        var logger = new QuietLogger();
        var asset = Assert.Single((await source.GetReleaseFeed(logger, "EterisBrowser", "win")).Assets);
        var file = Path.Combine(_dir, "pacote.nupkg");
        await Assert.ThrowsAsync<UpdateSignatureException>(() => source.DownloadReleaseEntry(logger, asset, file, _ => { }, default));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Download_WithTheSignedSha256_Succeeds()
    {
        var expected = Convert.ToHexString(SHA256.HashData("y"u8)).ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(Index(("0.1.1", expected)));
        var source = new SignedUpdateSource(new FeedSource(Index(("0.1.1", expected)), "y"u8.ToArray()), Fetch(bytes, Sign(_key, bytes)), [Pub(_key)]);
        var logger = new QuietLogger();
        var asset = Assert.Single((await source.GetReleaseFeed(logger, "EterisBrowser", "win")).Assets);
        var file = Path.Combine(_dir, "pacote.nupkg");
        await source.DownloadReleaseEntry(logger, asset, file, _ => { }, default);
        Assert.Equal("y"u8.ToArray(), File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Download_BeforeAnyVerifiedIndex_Throws()
    {
        var source = new SignedUpdateSource(new FeedSource(Index(("0.1.1", A)), "x"u8.ToArray()), Fetch([], null), [Pub(_key)]);
        var asset = VelopackAssetFeed.FromJson(Index(("0.1.1", A))).Assets[0];
        await Assert.ThrowsAsync<UpdateSignatureException>(() =>
            source.DownloadReleaseEntry(new QuietLogger(), asset, Path.Combine(_dir, "p.nupkg"), _ => { }, default));
    }

    [Fact]
    public void UpdateService_NeverUsesAnUnsignedSource()
    {
        var code = Source(Path.Combine("Updates", "UpdateService.cs"));
        Assert.DoesNotContain("new UpdateManager(new GithubSource(", code);
        Assert.Contains("try { return new UpdateManager(CreateSource()); }", code);
        Assert.Contains("new SignedUpdateSource(new GithubSource(RepoUrl, null, false), FetchLatestAsset, UpdateSignature.EmbeddedKeys)", code);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
