using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ThemedPagesTests
{
    private const string Page = """<!doctype html><html lang="pt"><head><meta name="color-scheme" content="dark"></head></html>""";

    [Fact]
    public void Light_MarksHtmlAndColorScheme()
    {
        var html = InternalPages.Themed(Page, light: true);
        Assert.Contains("""<html lang="pt" class="light">""", html);
        Assert.Contains("content=\"light\"", html);
    }

    [Fact]
    public void Dark_LeavesPageUntouched()
        => Assert.Equal(Page, InternalPages.Themed(Page, light: false));

    [Theory]
    [InlineData("newtab")]
    [InlineData("history")]
    [InlineData("settings")]
    [InlineData("bookmarks")]
    [InlineData("error")]
    [InlineData("passwords")]
    public void EveryInternalPage_HasLightStyles(string page)
    {
        using var stream = typeof(InternalPages).Assembly.GetManifestResourceStream($"Eteris.Browser.Browser.{page}.html")!;
        using var reader = new StreamReader(stream);
        Assert.Contains("html.light {", reader.ReadToEnd());
    }
}

public class WelcomePageTests
{
    [Fact]
    public void Render_CarriesChoicesAndImports()
    {
        var html = Eteris.Browser.Browser.WelcomePage.Render(new Eteris.Browser.Data.UserSettings { Theme = "light" },
            [new Eteris.Browser.Data.ImportSource("chrome", "Google Chrome", "x", 12)]);
        Assert.Contains("\"theme\":\"light\"", html);
        Assert.Contains("\"count\":12", html);
        Assert.Contains(Eteris.Browser.Browser.InternalPages.Token, html);
    }

    [Fact]
    public void WelcomePage_CanOpenFromCommandLine()
        => Assert.NotNull(Eteris.Browser.Infrastructure.StartupArgs.InitialUrl(["eteris://welcome/"]));
}
