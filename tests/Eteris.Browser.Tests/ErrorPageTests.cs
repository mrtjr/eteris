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
    [InlineData(-102, "O site não respondeu")]
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
}
