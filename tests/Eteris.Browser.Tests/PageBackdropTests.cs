using System.IO;
using System.Windows.Media;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

public class PageBackdropTests
{
    [Theory]
    [InlineData("https://pt.wikipedia.org/wiki/Lisboa")]
    [InlineData("http://exemplo.pt/")]
    public void WebPages_GetWhiteUnderneath_LikeChrome(string address)
    {
        Assert.Equal(Colors.White, PageBackdrop.For(address, lightTheme: false, darkenedSite: false));
        Assert.Equal(Colors.White, PageBackdrop.For(address, lightTheme: true, darkenedSite: false));
    }

    [Fact]
    public void DarkenedSites_GetTheInvertedWhite()
        => Assert.Equal(Color.FromRgb(0x1A, 0x1A, 0x1A), PageBackdrop.For("https://a.pt/", lightTheme: true, darkenedSite: true));

    [Theory]
    [InlineData("eteris://newtab/")]
    [InlineData("eteris://settings/")]
    [InlineData("about:blank")]
    [InlineData(null)]
    public void InternalPages_FollowTheEterisTheme(string? address)
    {
        // As mesmas cores de --bg das páginas internas (settings.html, history.html…).
        Assert.Equal(Color.FromRgb(0x0B, 0x0D, 0x13), PageBackdrop.For(address, lightTheme: false, darkenedSite: false));
        Assert.Equal(Color.FromRgb(0xF3, 0xF4, 0xF9), PageBackdrop.For(address, lightTheme: true, darkenedSite: false));
    }

    [Fact]
    public void InternalPages_IgnoreTheDarkSiteFlag()
        => Assert.Equal(PageBackdrop.InternalDark, PageBackdrop.For("eteris://newtab/", lightTheme: false, darkenedSite: true));

    [Fact]
    public void InternalPageColors_MatchTheirCss()
    {
        var html = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Browser", "settings.html"));
        Assert.Contains("--bg: #0b0d13", html);
        Assert.Contains("--bg: #f3f4f9", html);
    }

    [Theory]
    [InlineData(0x16C70000, 0x14C70000)]
    [InlineData(0x06C70000, 0x04C70000)]
    [InlineData(0x14C70000, 0x14C70000)]
    public void MainWindow_StopsClippingItsChildWindows(long style, long expected)
        => Assert.Equal((nint)expected, WindowEffects.WithoutClipChildren((nint)style));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
