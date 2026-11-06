using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-settings-" + Guid.NewGuid() + ".json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Defaults_ArePrivacyFirst()
    {
        var s = new UserSettings();
        Assert.Equal("duckduckgo", s.SearchEngine);
        Assert.Equal("restore", s.Startup);
        Assert.Equal(20, s.SleepMinutes);
        Assert.True(s.HttpsFirst);
        Assert.True(s.BlockThirdPartyCookies);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var store = new SettingsStore(_path);
        store.Save(new UserSettings { SearchEngine = "brave", SleepMinutes = 5 });
        var loaded = new SettingsStore(_path).Load();
        Assert.Equal("brave", loaded.SearchEngine);
        Assert.Equal(5, loaded.SleepMinutes);
    }

    [Fact]
    public void MissingOrCorruptFile_GivesDefaults()
    {
        Assert.Equal(new UserSettings(), new SettingsStore(_path).Load());
        File.WriteAllText(_path, "{ not json");
        Assert.Equal(new UserSettings(), new SettingsStore(_path).Load());
    }

    [Theory]
    [InlineData("engine", "brave")]
    [InlineData("engine", "startpage")]
    [InlineData("startup", "newtab")]
    [InlineData("sleep", "5")]
    [InlineData("sleep", "0")]
    [InlineData("httpsFirst", "false")]
    [InlineData("cookies", "false")]
    [InlineData("mica", "false")]
    public void With_AcceptsValidValues(string key, string value)
        => Assert.NotNull(new UserSettings().With(key, value));

    [Theory]
    [InlineData("engine", "evil")]
    [InlineData("startup", "explode")]
    [InlineData("sleep", "abc")]
    [InlineData("sleep", "-3")]
    [InlineData("httpsFirst", "talvez")]
    [InlineData("unknown", "x")]
    public void With_RejectsInvalidValues(string key, string value)
        => Assert.Null(new UserSettings().With(key, value));

    [Fact]
    public void With_ChangesOnlyThatSetting()
    {
        var s = new UserSettings().With("sleep", "60")!;
        Assert.Equal(60, s.SleepMinutes);
        Assert.Equal("duckduckgo", s.SearchEngine);
    }

    [Theory]
    [InlineData("brave", "Brave Search")]
    [InlineData("startpage", "Startpage")]
    [InlineData("nope", "DuckDuckGo")]
    public void SearchEngine_ById(string id, string name)
        => Assert.Equal(name, SearchEngine.ById(id).Name);

    [Fact]
    public void Sleep_NeverWhenThresholdIsZero()
        => Assert.False(SleepPolicy.ShouldSleep(false, false, false, false, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, TimeSpan.Zero));

    [Fact]
    public void Sleep_UsesCustomThreshold()
        => Assert.True(SleepPolicy.ShouldSleep(false, false, false, false, DateTime.UtcNow.AddMinutes(-6), DateTime.UtcNow, TimeSpan.FromMinutes(5)));
}

public class SettingsPageTests
{
    [Fact]
    public void Render_EmbedsCurrentChoicesAndToken()
    {
        var html = Eteris.Browser.Browser.SettingsPage.Render(new UserSettings { SearchEngine = "brave", SleepMinutes = 0 });
        Assert.Contains("\"engine\":\"brave\"", html);
        Assert.Contains("\"sleep\":0", html);
        Assert.Contains(Eteris.Browser.Browser.InternalPages.Token, html);
        Assert.DoesNotContain("{{DATA}}", html);
    }
}
