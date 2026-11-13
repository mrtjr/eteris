using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Omnibox;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-settings-" + Guid.NewGuid() + ".json");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
        File.Delete(_path + ".bak");
        File.Delete(_path + ".corrupt");
    }

    [Fact]
    public void Defaults_ArePrivacyFirst()
    {
        var s = new UserSettings();
        Assert.Equal("duckduckgo", s.SearchEngine);
        Assert.Equal("restore", s.Startup);
        Assert.Equal(20, s.SleepMinutes);
        Assert.True(s.HttpsFirst);
        Assert.True(s.BlockThirdPartyCookies);
        Assert.False(s.SearchSuggestions);
        Assert.Equal("coast", s.NewTabWallpaper);
        Assert.Equal("photo", s.NewTabWallpaperMotion);
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

    [Theory]
    [InlineData("photo")]
    [InlineData("live")]
    [InlineData("live-always")]
    public void WallpaperMotion_ValidChoiceSurvivesReload(string mode)
    {
        var updated = Assert.IsType<UserSettings>(new UserSettings().With("ntWallpaperMotion", mode));
        var store = new SettingsStore(_path);
        store.Save(updated);
        Assert.Equal(mode, store.Load().NewTabWallpaperMotion);
    }

    [Theory]
    [InlineData("coast")]
    [InlineData("mountains")]
    [InlineData("none")]
    public void Wallpaper_ValidChoiceSurvivesReload(string wallpaper)
    {
        var updated = Assert.IsType<UserSettings>(new UserSettings().With("ntWallpaper", wallpaper));
        Assert.Equal(wallpaper, updated.NewTabWallpaper);
        var store = new SettingsStore(_path);
        store.Save(updated);
        Assert.Equal(wallpaper, store.Load().NewTabWallpaper);
    }

    // A marca «oferta de entrar com o Google já mostrada» (0.2.15, T13): falsa por omissão, a página não a muda, e um
    // ficheiro antigo sem ela carrega com falso.
    [Fact]
    public void SignInOffered_IsLocal_AndThePageCannotSetIt()
    {
        Assert.False(new UserSettings().SignInOffered);
        Assert.Null(new UserSettings().With("signInOffered", "true"));
        Assert.Null(new UserSettings().With("SignInOffered", "true"));
        File.WriteAllText(_path, "{\"SearchEngine\":\"brave\"}");
        var loaded = new SettingsStore(_path).Load();
        Assert.Equal("brave", loaded.SearchEngine);
        Assert.False(loaded.SignInOffered);
        new SettingsStore(_path).Save(loaded with { SignInOffered = true });
        Assert.True(new SettingsStore(_path).Load().SignInOffered);
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
    [InlineData("engine", "google")]
    [InlineData("startup", "newtab")]
    [InlineData("sleep", "5")]
    [InlineData("sleep", "0")]
    [InlineData("httpsFirst", "false")]
    [InlineData("cookies", "false")]
    [InlineData("mica", "false")]
    [InlineData("verticalTabs", "true")]
    [InlineData("autoplay", "false")]
    [InlineData("suggest", "true")]
    [InlineData("clearOnExit", "true")]
    [InlineData("cleanUrls", "false")]
    [InlineData("ntTheme", "sunset")]
    [InlineData("ntWallpaper", "mountains")]
    [InlineData("ntWallpaper", "none")]
    [InlineData("ntTiles", "false")]
    [InlineData("sidebarCollapsed", "true")]
    public void With_AcceptsValidValues(string key, string value)
        => Assert.NotNull(new UserSettings().With(key, value));

    [Theory]
    [InlineData("engine", "evil")]
    [InlineData("startup", "explode")]
    [InlineData("sleep", "abc")]
    [InlineData("sleep", "-3")]
    [InlineData("httpsFirst", "talvez")]
    [InlineData("unknown", "x")]
    [InlineData("ntTheme", "neon")]
    [InlineData("ntWallpaper", "galaxy")]
    [InlineData("ntWallpaper", "Coast")]
    [InlineData("ntWallpaperMotion", "auto")]
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
    [InlineData("google", "Google")]
    [InlineData("nope", "DuckDuckGo")]
    public void SearchEngine_ById(string id, string name)
        => Assert.Equal(name, SearchEngine.ById(id).Name);

    // O Google é uma escolha, nunca o padrão; pesquisa em www.google.com e o atalho !g usa o mesmo motor.
    [Fact]
    public void Google_IsAChoice_NotTheDefault()
    {
        Assert.Equal("https://www.google.com/search?q=fado%20lisboa", SearchEngine.Google.UrlFor("fado lisboa"));
        Assert.Equal(SearchEngine.DuckDuckGo, SearchEngine.Default);
        Assert.Equal("duckduckgo", new UserSettings().SearchEngine);
        Assert.Same(SearchEngine.Google, Bangs.Find("!g")!.Engine);
    }

    // Cada motor que as definições aceitam tem o seu botão nas Definições (e só esses), e o texto diz o que o Google faz
    // com as pesquisas. As Boas-vindas dizem «Nenhum deles cria um perfil seu»: lá ficam só os motores que não o fazem.
    [Fact]
    public void EveryEngine_HasItsButtonInSettings_AndWelcomeKeepsOnlyThePrivateOnes()
    {
        var settings = Page("settings.html");
        Assert.Equal(UserSettings.Engines.Order(), EngineButtons(settings).Order());
        Assert.Contains("Com o Google, as pesquisas vão para a Google, que as guarda e as pode juntar à sua conta Google se tiver sessão iniciada nela neste navegador.", settings);
        // As sugestões enquanto escreve só existem com o DuckDuckGo e o Brave (SearchSuggest.EndpointFor).
        Assert.Contains("Só com o DuckDuckGo e o Brave.", settings);

        var welcome = Page("welcome.html");
        Assert.Equal(["brave", "duckduckgo", "startpage"], EngineButtons(welcome).Order());
        Assert.Contains("Nenhum deles cria um perfil seu", welcome);
    }

    private static string Page(string name) => File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Browser", name));

    private static IEnumerable<string> EngineButtons(string html)
    {
        var seg = System.Text.RegularExpressions.Regex.Match(html, "data-key=\"engine\"[^>]*>(?<b>.*?)</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline).Groups["b"].Value;
        return System.Text.RegularExpressions.Regex.Matches(seg, "data-value=\"([a-z]+)\"").Select(m => m.Groups[1].Value);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    [Fact]
    public void Sleep_NeverWhenThresholdIsZero()
        => Assert.False(SleepPolicy.ShouldSleep(false, false, false, false, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, TimeSpan.Zero));

    [Fact]
    public void Sleep_UsesCustomThreshold()
        => Assert.True(SleepPolicy.ShouldSleep(false, false, false, false, DateTime.UtcNow.AddMinutes(-6), DateTime.UtcNow, TimeSpan.FromMinutes(5)));
    // DAD-07: um settings.json estragado repunha as predefinições (incluindo as de privacidade) em silêncio.
    [Fact]
    public void Load_Corrupt_UsesBakAndKeepsCorruptCopy()
    {
        var store = new SettingsStore(_path);
        store.Save(new UserSettings { SearchEngine = "brave" });
        store.Save(new UserSettings { SearchEngine = "startpage" });
        File.WriteAllText(_path, "{ not json");
        Assert.Equal("brave", store.Load().SearchEngine);
        Assert.Equal(AtomicRead.FromBackup, store.LastLoad);
        Assert.Equal("{ not json", File.ReadAllText(_path + ".corrupt"));
    }

    [Fact]
    public void Save_AfterAnUnreadableLoad_SetsTheOldFileAside()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new SettingsStore(_path);
        Assert.Equal(new UserSettings(), store.Load());
        Assert.Equal(AtomicRead.Unreadable, store.LastLoad);
        store.Save(new UserSettings { SearchEngine = "brave" });
        Assert.Equal("{ not json", File.ReadAllText(_path + ".corrupt"));
        Assert.Equal("brave", new SettingsStore(_path).Load().SearchEngine);
    }

}

public class SettingsPageTests
{
    [Fact]
    public void Render_HasASearchBoxThatIgnoresAccents()
    {
        var html = Eteris.Browser.Browser.SettingsPage.Render(new UserSettings());
        Assert.Contains("id=\"find\"", html);
        Assert.Contains("normalize('NFD')", html);
        Assert.Contains("Nenhuma definição encontrada", html);
    }

    // Auditoria (INT-01): só «true» e «false» exatos, em minúsculas e sem espaços, mudam um interruptor.
    [Theory] [InlineData("False")] [InlineData("FALSE")] [InlineData(" false ")] [InlineData("True")] [InlineData("0")] [InlineData("")]
    public void With_OnlyExactTrueAndFalse(string value)
    {
        foreach (var key in new[] { "httpsFirst", "cookies", "cleanUrls", "debounce", "bounceForget", "cname", "notifications",
                     "clearOnExit", "suggest", "pwAskHello", "payAskHello", "pwOffer", "sleepPressure" })
            Assert.Null(new UserSettings().With(key, value));
        Assert.False(UserSettings.TryParseSwitch(value, out _));
    }

    [Theory] [InlineData("true", true)] [InlineData("false", false)]
    public void TryParseSwitch_AcceptsExactTrueAndFalse(string value, bool expected)
    {
        Assert.True(UserSettings.TryParseSwitch(value, out var on));
        Assert.Equal(expected, on);
        Assert.False(UserSettings.TryParseSwitch(null!, out _));
    }

    [Fact]
    public void With_NeverUsesBoolTryParse()
        => Assert.DoesNotContain("bool.TryParse", File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Data", "UserSettings.cs")));

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
