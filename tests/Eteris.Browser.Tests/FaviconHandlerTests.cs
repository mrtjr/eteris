using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class FaviconHandlerTests
{
    [Fact]
    public void PreferredFavicon_PrefersPngOverSvg()
        => Assert.Equal("https://example.com/icon.png",
            FaviconHandler.PreferredFavicon(["https://example.com/icon.svg", "https://example.com/icon.png"]));

    [Fact]
    public void PreferredFavicon_TakesIcoWhenItComesFirst()
        => Assert.Equal("https://example.com/favicon.ico",
            FaviconHandler.PreferredFavicon(["https://example.com/favicon.ico", "https://example.com/icon.png"]));

    [Fact]
    public void PreferredFavicon_OnlySvg_FallsBackToIt()
        => Assert.Equal("https://example.com/icon.svg", FaviconHandler.PreferredFavicon(["https://example.com/icon.svg"]));

    [Fact]
    public void PreferredFavicon_IgnoresQueryStringWhenLookingAtTheExtension()
        => Assert.Equal("https://example.com/favicon.ico?v=2",
            FaviconHandler.PreferredFavicon(["https://example.com/icon.svg", "https://example.com/favicon.ico?v=2"]));

    [Fact]
    public void PreferredFavicon_EmptyList_ReturnsNull()
        => Assert.Null(FaviconHandler.PreferredFavicon([]));

    [Theory]
    [InlineData(200, "image/png", 1234)]
    [InlineData(200, "image/x-icon", 50_000)]
    [InlineData(200, "image/vnd.microsoft.icon", 50_000)]
    [InlineData(200, null, 100)]
    [InlineData(200, "application/octet-stream", 100)]
    [InlineData(200, "image/png", FaviconHandler.MaxBytes)]
    public void AcceptsResponse_SuccessfulRasterResponse_IsUsable(int status, string? mime, long length)
        => Assert.True(FaviconHandler.AcceptsResponse(status, mime, length));

    [Theory]
    [InlineData(404, "image/png", 10)]
    [InlineData(500, "image/png", 10)]
    [InlineData(0, "image/png", 10)]
    [InlineData(200, "image/svg+xml", 500)]
    [InlineData(200, "text/html; charset=utf-8", 500)]
    [InlineData(200, "image/png", 0)]
    [InlineData(200, "image/png", FaviconHandler.MaxBytes + 1)]
    public void AcceptsResponse_FailedSvgHtmlEmptyOrOversized_IsNotUsable(int status, string? mime, long length)
        => Assert.False(FaviconHandler.AcceptsResponse(status, mime, length));
}
