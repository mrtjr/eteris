using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class ShieldExceptionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-shield-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Add_IsVisibleImmediatelyAndAfterReopening()
    {
        new ShieldExceptionStore(_path).Add("www.publico.pt");
        Assert.True(new ShieldExceptionStore(_path).Contains("publico.pt"));
    }

    [Fact]
    public void Contains_MatchesWholeSiteRegardlessOfSubdomain()
    {
        var store = new ShieldExceptionStore(_path);
        store.Add("publico.pt");
        Assert.True(store.Contains("www.publico.pt"));
        Assert.True(store.Contains("desporto.publico.pt"));
        Assert.False(store.Contains("expresso.pt"));
    }

    [Fact]
    public void Remove_RestoresProtection()
    {
        var store = new ShieldExceptionStore(_path);
        store.Add("publico.pt");
        store.Remove("www.publico.pt");
        Assert.False(new ShieldExceptionStore(_path).Contains("publico.pt"));
    }
}
