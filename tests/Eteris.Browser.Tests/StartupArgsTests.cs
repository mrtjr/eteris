using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class StartupArgsTests
{
    private static string[] Args(string joined) => joined.Length == 0 ? [] : joined.Split('|');

    [Theory]
    [InlineData("https://www.publico.pt/", "https://www.publico.pt/")]
    [InlineData("--squirrel-firstrun|http://localhost:8765/x.html", "http://localhost:8765/x.html")]
    public void InitialUrl_FirstWebAddress(string args, string expected)
        => Assert.Equal(expected, StartupArgs.InitialUrl(Args(args)));

    [Fact]
    public void UserDataDir_ReadsFlagValue()
        => Assert.Equal(@"C:\temp\perfil", StartupArgs.UserDataDir(Args(@"--user-data-dir=C:\temp\perfil|https://x.pt/")));

    [Theory]
    [InlineData("--private", true)]
    [InlineData("--incognito|https://x.pt/", true)]
    [InlineData("https://x.pt/", false)]
    public void IsPrivate_RecognisesFlags(string args, bool expected)
        => Assert.Equal(expected, StartupArgs.IsPrivate(Args(args)));

    [Fact]
    public void UserDataDir_AbsentIsNull()
        => Assert.Null(StartupArgs.UserDataDir(Args("https://x.pt/")));

    [Fact]
    public void InitialUrl_SkipsUserDataDirFlag()
        => Assert.Equal("https://x.pt/", StartupArgs.InitialUrl(Args(@"--user-data-dir=C:\p|https://x.pt/")));

    [Theory]
    [InlineData("")]
    [InlineData("--flag")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("eteris://newtab/")]
    [InlineData("eteris://history/clear?range=all")]
    [InlineData("eteris://history/delete")]
    [InlineData("eteris://passwords/reveal?id=x")]
    [InlineData("eteris://passwords/export")]
    public void InitialUrl_IgnoresAnythingElse(string args)
        => Assert.Null(StartupArgs.InitialUrl(Args(args)));

    [Theory]
    [InlineData("eteris://history/")]
    [InlineData("eteris://settings/")]
    [InlineData("eteris://about/")]
    [InlineData("eteris://passwords/")]
    [InlineData("eteris://autofill/")]
    public void InitialUrl_AllowsSafeInternalPages(string url)
        => Assert.Equal(url, StartupArgs.InitialUrl([url]));
}
