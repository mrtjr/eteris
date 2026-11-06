using System.IO;
using System.Net;
using System.Net.Http;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public sealed class FilterListsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-lists-" + Guid.NewGuid());
    private readonly string _bundled;
    private readonly string _cache;
    private readonly FilterLists _lists;

    public FilterListsTests()
    {
        _bundled = Directory.CreateDirectory(Path.Combine(_root, "bundled")).FullName;
        _cache = Path.Combine(_root, "cache");
        foreach (var name in FilterLists.Sources.Keys)
            File.WriteAllText(Path.Combine(_bundled, name + ".txt"), $"! embutida {name}\n");
        _lists = new FilterLists(_bundled, _cache);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string ValidList(string marker) => "! Title: lista\n" + string.Concat(Enumerable.Repeat($"||{marker}.example^\n", 200));

    [Fact]
    public void Sources_AreTheUBlockOriginLiteDefaultsPlusPortuguese()
    {
        Assert.Contains("easylist", FilterLists.Sources.Keys);
        Assert.Contains("easyprivacy", FilterLists.Sources.Keys);
        Assert.Contains("ublock-filters", FilterLists.Sources.Keys);
        Assert.Contains("easylist-portuguese", FilterLists.Sources.Keys);
        Assert.All(FilterLists.Sources.Values, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public void Sources_IncludeUBlockCookieNoticeLists()
    {
        Assert.Contains("ublock-cookies-easylist", FilterLists.Sources.Keys);
        Assert.Contains("ublock-cookies-adguard", FilterLists.Sources.Keys);
    }

    [Fact]
    public void Load_SplitsTrustedUBlockListsFromThirdPartyLists()
    {
        var trusted = _lists.Load(trusted: true);
        var normal = _lists.Load(trusted: false);
        Assert.Contains("! embutida ublock-filters", trusted);
        Assert.Contains("! embutida ublock-cookies-adguard", trusted);
        Assert.DoesNotContain("! embutida easylist\n", trusted);
        Assert.Contains("! embutida easylist\n", normal);
        Assert.DoesNotContain("ublock-", normal);
    }

    [Fact]
    public void LoadAll_WithoutCache_UsesBundledCopies()
    {
        var text = _lists.LoadAll();
        Assert.Contains("! embutida easylist", text);
        Assert.Contains("! embutida ublock-filters", text);
    }

    [Fact]
    public void LoadAll_PrefersDownloadedCopy()
    {
        Directory.CreateDirectory(_cache);
        File.WriteAllText(Path.Combine(_cache, "easylist.txt"), "! descarregada\n");
        var text = _lists.LoadAll();
        Assert.Contains("! descarregada", text);
        Assert.DoesNotContain("! embutida easylist\n", text);
        Assert.Contains("! embutida easyprivacy", text);
    }

    [Fact]
    public void IsStale_WhenNeverDownloaded()
        => Assert.True(_lists.IsStale(Now));

    [Fact]
    public void IsStale_FreshDownloadIsNotStaleForAWeek()
    {
        Directory.CreateDirectory(_cache);
        foreach (var name in FilterLists.Sources.Keys)
        {
            var path = Path.Combine(_cache, name + ".txt");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, Now.AddDays(-6));
        }
        Assert.False(_lists.IsStale(Now));
        Assert.True(_lists.IsStale(Now.AddDays(2)));
    }

    [Fact]
    public async Task UpdateAsync_StoresEveryValidList()
    {
        using var http = new HttpClient(new FakeHandler(_ => (HttpStatusCode.OK, ValidList("novo"))));
        Assert.True(await _lists.UpdateAsync(http, CancellationToken.None));
        Assert.Contains("novo.example", _lists.LoadAll());
        Assert.False(_lists.IsStale(DateTime.UtcNow));
    }

    [Fact]
    public async Task UpdateAsync_RejectsErrorPagesAndKeepsPreviousCopy()
    {
        Directory.CreateDirectory(_cache);
        File.WriteAllText(Path.Combine(_cache, "easylist.txt"), ValidList("antigo"));
        using var http = new HttpClient(new FakeHandler(_ => (HttpStatusCode.OK, "<!doctype html><title>Erro</title>")));
        Assert.False(await _lists.UpdateAsync(http, CancellationToken.None));
        Assert.Contains("antigo.example", _lists.LoadAll());
    }

    [Fact]
    public async Task UpdateAsync_NetworkFailureIsNotAnException()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("sem rede")));
        Assert.False(await _lists.UpdateAsync(http, CancellationToken.None));
        Assert.Contains("! embutida easylist", _lists.LoadAll());
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void Summary_CountsRulesAndMarksBundledCopies()
    {
        Directory.CreateDirectory(_cache);
        File.WriteAllText(Path.Combine(_cache, "easylist.txt"), "[Adblock Plus 2.0]\n! comentário\n||a.example^\n\n##.anuncio\n");
        File.SetLastWriteTimeUtc(Path.Combine(_cache, "easylist.txt"), Now.AddDays(-2));

        var summary = _lists.Summary();
        var easylist = summary.Single(s => s.Name == "easylist");
        Assert.Equal(2, easylist.Rules);
        Assert.False(easylist.Bundled);
        Assert.Equal(Now.AddDays(-2), easylist.UpdatedUtc);

        var bundled = summary.Single(s => s.Name == "easyprivacy");
        Assert.True(bundled.Bundled);
        Assert.Equal(0, bundled.Rules);
        Assert.Equal(FilterLists.Sources.Count, summary.Count);
    }
}

public class UpdateTextTests
{
    [Theory]
    [InlineData(Eteris.Browser.Updates.UpdateState.Development, null, "Versão de desenvolvimento: as atualizações só funcionam na versão instalada.")]
    [InlineData(Eteris.Browser.Updates.UpdateState.Checking, null, "A procurar atualizações…")]
    [InlineData(Eteris.Browser.Updates.UpdateState.UpToDate, null, "O Éteris está atualizado.")]
    [InlineData(Eteris.Browser.Updates.UpdateState.Ready, "0.2.0", "A versão 0.2.0 está pronta: é instalada quando fechar o Éteris.")]
    [InlineData(Eteris.Browser.Updates.UpdateState.Failed, null, "Não foi possível procurar atualizações (sem ligação?).")]
    public void Describe_IsPlainPortuguese(Eteris.Browser.Updates.UpdateState state, string? version, string text)
        => Assert.Equal(text, Eteris.Browser.Updates.UpdateService.Describe(state, version));
}
