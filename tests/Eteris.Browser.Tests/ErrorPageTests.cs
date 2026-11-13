using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ErrorPageTests
{
    [Fact]
    public void RendererCrash_HasFriendlyText()
    {
        var text = ErrorPage.Describe(ErrorPage.RendererCrashed);
        Assert.Equal("Esta página foi abaixo", text.Title);
        Assert.Contains("outras abas", text.Detail);
    }

    [Theory]
    [InlineData(-105, "Não encontrámos este site")]
    [InlineData(-106, "Está sem ligação à Internet")]
    [InlineData(-102, "O site recusou a ligação")]
    [InlineData(-7, "O site não respondeu")]
    [InlineData(-118, "O site não respondeu")]
    [InlineData(-201, "A ligação não é segura")]
    [InlineData(-999, "Não foi possível abrir a página")]
    public void Describe_MapsChromiumErrors(int code, string title)
        => Assert.Equal(title, ErrorPage.Describe(code).Title);

    [Fact]
    public void Url_RoundTripsFailedAddressAndCode()
    {
        var url = ErrorPage.Url(-105, "https://nao-existe.pt/a?b=1&c=2");
        Assert.True(ErrorPage.IsErrorPage(url));
        Assert.Equal("https://nao-existe.pt/a?b=1&c=2", ErrorPage.FailedUrl(url));
        Assert.Equal(-105, ErrorPage.Code(url));
    }

    [Fact]
    public void IsErrorPage_OnlyForTheInternalPage()
    {
        Assert.False(ErrorPage.IsErrorPage("https://error.pt/"));
        Assert.False(ErrorPage.IsErrorPage("eteris://newtab/"));
    }

    [Fact]
    public void Render_EmbedsDataSafelyAndOffersRetryOnlyForWebAddresses()
    {
        var html = ErrorPage.Render(-105, "https://x.pt/\"</script><script>alert(1)</script>");
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("\"retry\":true", html);
        Assert.Contains("\"retry\":false", ErrorPage.Render(-105, "javascript:alert(1)"));
    }

    [Fact]
    public void Render_Offline_IncludesTheGame()
        => Assert.Contains("\"offline\":true", ErrorPage.Render(-106, "https://x.pt/"));

    [Theory]
    [InlineData(-3, false)]
    [InlineData(-105, true)]
    [InlineData(-106, true)]
    public void ShouldShow_IgnoresAbortedLoads(int code, bool expected)
        => Assert.Equal(expected, ErrorPage.ShouldShow(code));

    // INT-05: o parâmetro «url» da página de erro é texto da página; só um endereço da web (ou de ficheiro) volta a abrir.
    [Theory]
    [InlineData("https://x.pt/a?b=1")]
    [InlineData("http://x.pt/")]
    [InlineData("file:///C:/a.html")]
    public void ReloadTarget_ReturnsTheFailedWebAddress(string failed)
        => Assert.Equal(failed, ErrorPage.ReloadTarget(ErrorPage.Url(-105, failed)));

    [Theory]
    [InlineData("eteris://error/?code=-105&url=javascript:alert(1)")]
    [InlineData("eteris://error/?code=-105&https=1&url=data:text/html,x")]
    [InlineData("eteris://error/?code=-105&url=eteris%3A%2F%2Fsettings%2F")]
    [InlineData("eteris://error/?code=-105")]
    [InlineData("https://x.pt/")]
    public void ReloadTarget_RefusesOtherSchemes(string address) => Assert.Null(ErrorPage.ReloadTarget(address));

    [Fact]
    public void FailedUrl_IsNeverLoaded()
    {
        foreach (var file in new[] { "MainWindow.Reload.cs", "MainWindow.xaml.cs", "MainWindow.WebPanels.cs", "MainWindow.Crash.cs", "MainWindow.VideoEngine.cs" })
            Assert.DoesNotContain("ErrorPage.FailedUrl(", File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", file)));
        var reload = File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.Reload.cs"));
        Assert.Contains("ErrorPage.ReloadTarget(", reload);
        Assert.DoesNotContain("browser.Load(tab.Address)", reload);   // o F5 carregava o que estivesse em tab.Address
    }
}
