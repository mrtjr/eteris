using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// O motor do Escudo lê as listas dos ficheiros e guarda-se compilado no perfil (motor-<resumo>.dat): o arranque seguinte
// não volta a compilar ~8 M carateres de regras. O nome muda com as listas e com a versão.
public sealed class ShieldEngineCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-motor-" + Guid.NewGuid());
    private readonly string _bundled;
    private readonly string _cache;
    private readonly FilterLists _lists;

    public ShieldEngineCacheTests()
    {
        _bundled = Directory.CreateDirectory(Path.Combine(_root, "bundled")).FullName;
        _cache = Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;
        foreach (var name in FilterLists.Sources.Keys)
            File.WriteAllText(Path.Combine(_bundled, name + ".txt"), $"! embutida {name}\n");
        File.WriteAllText(Path.Combine(_bundled, "easylist.txt"), "||doubleclick.net^\npublico.pt##.ad-slot\n");
        File.WriteAllText(Path.Combine(_bundled, "ublock-filters.txt"), "@@||doubleclick.net/allowed/$script\n");
        _lists = new FilterLists(_bundled, _cache);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Paths_AreTheListsInUseSplitByTrust()
    {
        File.WriteAllText(Path.Combine(_cache, "easyprivacy.txt"), "! descarregada\n");
        var normal = _lists.Paths(trusted: false);
        var trusted = _lists.Paths(trusted: true);
        Assert.Contains(Path.Combine(_cache, "easyprivacy.txt"), normal);
        Assert.Contains(Path.Combine(_bundled, "easylist.txt"), normal);
        Assert.All(trusted, path => Assert.StartsWith("ublock-", Path.GetFileName(path)));
        Assert.DoesNotContain(normal, path => Path.GetFileName(path).StartsWith("ublock-", StringComparison.Ordinal));
        Assert.Equal(FilterLists.Sources.Count, normal.Count + trusted.Count);
    }

    [Fact]
    public void EngineCachePath_ChangesWithAListOrTheVersion()
    {
        var first = _lists.EngineCachePath("1");
        Assert.Equal(first, _lists.EngineCachePath("1"));
        Assert.StartsWith(Path.Combine(_cache, "motor-"), first);
        Assert.NotEqual(first, _lists.EngineCachePath("2"));
        File.WriteAllText(Path.Combine(_cache, "easylist.txt"), "||outro.example^\n");
        Assert.NotEqual(first, _lists.EngineCachePath("1"));
    }

    [Fact]
    public void CreateFromFiles_SecondStartReadsTheCompiledEngine()
    {
        var cache = _lists.EngineCachePath("1");
        using (var compiled = AdblockEngine.CreateFromFiles(_lists.Paths(false), _lists.Paths(true), null, cache, out var origin))
        {
            Assert.Equal(EngineOrigin.Compiled, origin);
            // Compilar não grava: o motor entra em uso primeiro e grava-se depois, fora do caminho da 1.ª página.
            Assert.False(File.Exists(cache));
            AssertAnswers(compiled);
            Assert.True(compiled.SaveCache(cache));
            Assert.True(File.Exists(cache));
        }
        using var cached = AdblockEngine.CreateFromFiles(_lists.Paths(false), _lists.Paths(true), null, cache, out var again);
        Assert.Equal(EngineOrigin.Cache, again);
        AssertAnswers(cached);
    }

    // Uma lista que existe mas não se lê nesse arranque (o antivírus tem-na aberta com bloqueio): o motor serve sem ela
    // nesta sessão, mas não vai para a cache (senão os arranques seguintes ficavam sem a lista até ela mudar) e as caches
    // antigas ficam.
    [Fact]
    public void Create_WithALockedList_DoesNotCacheThePartialEngine()
    {
        var stale = Path.Combine(_cache, "motor-0000000000000000.dat");
        File.WriteAllText(stale, "antigo");
        var locked = Path.Combine(_bundled, "easyprivacy.txt");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var partial = ShieldEngine.Create(_lists);
            Assert.True(partial.ShouldBlock("https://ad.doubleclick.net/x.js", "https://www.publico.pt/", "script"));
            Assert.Equal([stale], Directory.GetFiles(_cache, "motor-*"));
        }
        using var whole = ShieldEngine.Create(_lists);
        Assert.Equal(_lists.EngineCachePath(ShieldEngine.EngineVersion()), Assert.Single(Directory.GetFiles(_cache, "motor-*")));
        using var again = AdblockEngine.CreateFromFiles(_lists.Paths(false), _lists.Paths(true), null, _lists.EngineCachePath(ShieldEngine.EngineVersion()), out var origin);
        Assert.Equal(EngineOrigin.Cache, origin);
    }

    [Fact]
    public void SaveCache_ToAMissingFolder_ReportsFailure()
    {
        using var engine = AdblockEngine.CreateFromFiles(_lists.Paths(false), _lists.Paths(true), null, null, out _);
        Assert.False(engine.SaveCache(Path.Combine(_root, "nao-existe", "motor.dat")));
    }

    // No arranque, a página principal espera pelo motor só enquanto ele não chegou, e só num site da rede com escudo.
    [Theory]
    [InlineData(true, "https://www.publico.pt/", true, true)]
    [InlineData(true, "http://www.publico.pt/", true, true)]
    [InlineData(false, "https://www.publico.pt/", true, false)]
    [InlineData(true, "https://www.publico.pt/", false, false)]
    [InlineData(true, "eteris://newtab/", true, false)]
    [InlineData(true, "data:text/html,ola", true, false)]
    [InlineData(true, "nao e um endereco", true, false)]
    public void ShouldHold_OnlyWhileTheEngineIsMissingOnAProtectedWebPage(bool missing, string url, bool shieldOn, bool hold)
        => Assert.Equal(hold, ShieldEngine.ShouldHold(missing, url, _ => shieldOn));

    [Fact]
    public void Create_RemovesEnginesOfOldLists()
    {
        var stale = Path.Combine(_cache, "motor-0000000000000000.dat");
        File.WriteAllText(stale, "antigo");
        File.WriteAllText(Path.Combine(_cache, "easylist.txt"), "||doubleclick.net^\n");
        using var engine = ShieldEngine.Create(_lists);
        Assert.False(File.Exists(stale));
        Assert.Single(Directory.GetFiles(_cache, "motor-*"));
        Assert.True(engine.ShouldBlock("https://ad.doubleclick.net/x.js", "https://www.publico.pt/", "script"));
    }

    [Fact]
    public void CreateFromFiles_WithoutCacheStillWorks()
    {
        using var engine = AdblockEngine.CreateFromFiles(_lists.Paths(false), _lists.Paths(true), null, null, out var origin);
        Assert.Equal(EngineOrigin.Compiled, origin);
        AssertAnswers(engine);
    }

    private static void AssertAnswers(AdblockEngine engine)
    {
        Assert.True(engine.ShouldBlock("https://ad.doubleclick.net/x.js", "https://www.publico.pt/", "script"));
        Assert.False(engine.ShouldBlock("https://ad.doubleclick.net/allowed/x.js", "https://www.publico.pt/", "script"));
        Assert.Equal(".ad-slot{display:none!important}\n", engine.UrlCss("https://www.publico.pt/"));
    }
}
