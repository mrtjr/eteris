using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public sealed class DailyStatsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-daily-" + Guid.NewGuid() + ".db");
    private static readonly DateTime Today = new(2026, 11, 6);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Add_AccumulatesPerDay()
    {
        var store = new DailyStatsStore(_path);
        store.Add(Today, 5);
        store.Add(Today, 3);
        store.Add(Today.AddDays(-1), 2);
        Assert.Equal([0, 0, 0, 0, 0, 2, 8], new DailyStatsStore(_path).Week(Today));
    }

    [Fact]
    public void Week_IgnoresOlderDays()
    {
        var store = new DailyStatsStore(_path);
        store.Add(Today.AddDays(-7), 100);
        store.Add(Today.AddDays(-6), 1);
        Assert.Equal(1, store.Week(Today).Sum());
        Assert.Equal(1, store.Week(Today)[0]);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(10, "≈ 300 KB e 1 s poupados")]
    [InlineData(4000, "≈ 120 MB e 3 min poupados")]
    [InlineData(100000, "≈ 3 GB e 1 h 23 min poupados")]
    public void Savings_AreReadable(int blocked, string text)
        => Assert.Equal(text, PrivacyEstimate.Describe(blocked));

    [Fact]
    public void DayInitials_EndToday()
        => Assert.Equal(["S", "D", "S", "T", "Q", "Q", "S"], PrivacyEstimate.DayInitials(Today));
}
