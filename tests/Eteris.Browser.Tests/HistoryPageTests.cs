using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class HistoryVisitsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-visits-" + Guid.NewGuid() + ".db");
    private readonly HistoryStore _store;
    private static readonly DateTime T0 = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public HistoryVisitsTests() => _store = new HistoryStore(_path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Visits_AreNewestFirstWithTimes()
    {
        _store.Record("https://a.pt/", "A", T0);
        _store.Record("https://b.pt/", "B", T0.AddMinutes(5));
        var visits = _store.Visits(100);
        Assert.Equal(["https://b.pt/", "https://a.pt/"], visits.Select(v => v.Url));
        Assert.Equal(T0.AddMinutes(5), visits[0].VisitedUtc);
    }

    [Fact]
    public void DeleteVisit_RemovesOnlyThatVisit()
    {
        _store.Record("https://a.pt/", "A", T0);
        _store.Record("https://a.pt/", "A", T0.AddMinutes(1));
        _store.DeleteVisit(_store.Visits(10)[0].Id);
        Assert.Single(_store.Visits(10));
    }

    [Fact]
    public void DeleteSince_KeepsOlderVisits()
    {
        _store.Record("https://old.pt/", "", T0.AddHours(-3));
        _store.Record("https://new.pt/", "", T0);
        _store.DeleteSince(T0.AddHours(-1));
        Assert.Equal("https://old.pt/", Assert.Single(_store.Visits(10)).Url);
    }

    [Fact]
    public void DeleteSinceMinValue_ClearsEverything()
    {
        _store.Record("https://a.pt/", "", T0);
        _store.DeleteSince(DateTime.MinValue);
        Assert.Empty(_store.Visits(10));
    }
}

public class HistoryPageTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 15, 0, 0);

    [Fact]
    public void DayLabels_AreFriendly()
    {
        Assert.Equal("Hoje", HistoryPage.DayLabel(Now.Date, Now));
        Assert.Equal("Ontem", HistoryPage.DayLabel(Now.Date.AddDays(-1), Now));
        Assert.Equal("terça-feira, 3 de novembro", HistoryPage.DayLabel(new DateTime(2026, 11, 3), Now));
        Assert.Equal("quarta-feira, 5 de novembro de 2025", HistoryPage.DayLabel(new DateTime(2025, 11, 5), Now));
    }

    [Theory]
    [InlineData("hour", 2026, 11, 5, 14)]
    [InlineData("today", 2026, 11, 5, 0)]
    public void ClearRange_ResolvesToLocalStart(string range, int y, int m, int d, int h)
        => Assert.Equal(new DateTime(y, m, d, h, 0, 0), HistoryPage.RangeStart(range, Now));

    [Fact]
    public void ClearAll_IsMinValue_AndUnknownIsNull()
    {
        Assert.Equal(DateTime.MinValue, HistoryPage.RangeStart("all", Now));
        Assert.Null(HistoryPage.RangeStart("forever-and-ever", Now));
    }

    [Fact]
    public void Actions_RequireSessionToken()
    {
        Assert.True(HistoryPage.IsAuthorized($"eteris://history/delete?id=4&t={HistoryPage.Token}"));
        Assert.False(HistoryPage.IsAuthorized("eteris://history/delete?id=4&t=wrong"));
        Assert.False(HistoryPage.IsAuthorized("eteris://history/delete?id=4"));
    }

    [Fact]
    public void Render_GroupsByDayAndEscapesTitles()
    {
        var visits = new[]
        {
            new Visit(2, "https://a.pt/", "<script>x</script>", Now.AddHours(-1).ToUniversalTime()),
            new Visit(1, "https://b.pt/", "B", Now.AddDays(-1).ToUniversalTime()),
        };
        var html = HistoryPage.Render(visits, Now);
        Assert.Contains("Hoje", html);
        Assert.Contains("Ontem", html);
        Assert.DoesNotContain("<script>x</script>", html);
        Assert.Contains(HistoryPage.Token, html);
    }

    // A página lê data.icons[item.iconKey]: a chave por visita tem de ser a mesma que indexa o dicionário.
    [Fact]
    public void Render_EmitsIconKeysThatIndexTheIconDictionary()
    {
        var visits = new[]
        {
            new Visit(2, "https://a.pt/docs/", "Docs", Now.AddHours(-1).ToUniversalTime()),
            new Visit(1, "file:///x", "Local", Now.AddHours(-2).ToUniversalTime()),
        };
        var icons = new Dictionary<string, string> { ["https://a.pt"] = "data:image/png;base64,AAAA" };
        var html = HistoryPage.Render(visits, Now, icons);
        Assert.Contains("\"iconKey\":\"https://a.pt\"", html);
        Assert.Contains("\"iconKey\":null", html);
        Assert.Contains("\"icons\":{\"https://a.pt\":\"data:image/png;base64,AAAA\"}", html);
    }

    [Fact]
    public void Render_WithoutIcons_EmitsAnEmptyDictionary()
    {
        var html = HistoryPage.Render([new Visit(1, "https://a.pt/", "A", Now.ToUniversalTime())], Now);
        Assert.Contains("\"icons\":{}", html);
        Assert.DoesNotContain("\"icons\":null", html);
    }
}
