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

    [Theory]
    [InlineData("")]
    [InlineData("--flag")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("eteris://newtab/")]
    public void InitialUrl_IgnoresAnythingElse(string args)
        => Assert.Null(StartupArgs.InitialUrl(Args(args)));
}
