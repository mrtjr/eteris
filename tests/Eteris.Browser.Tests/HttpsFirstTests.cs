using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class HttpsFirstTests
{
    [Theory]
    [InlineData("http://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://www.publico.pt/", "https://www.publico.pt/")]
    [InlineData("HTTP://Example.com", "https://example.com/")]
    public void Upgrade_PublicHttpBecomesHttps(string url, string expected)
        => Assert.Equal(expected, new HttpsFirst().Upgrade(url));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://localhost:3000/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://meu-router/")]
    [InlineData("http://example.com:8080/")]
    [InlineData("eteris://newtab/")]
    public void Upgrade_LeavesLocalCustomPortAndSecureAlone(string url)
        => Assert.Null(new HttpsFirst().Upgrade(url));

    [Fact]
    public void Fallback_RemembersHostForTheSession()
    {
        var https = new HttpsFirst();
        https.Upgrade("http://insecure.example/x");
        Assert.Equal("http://insecure.example/x", https.FallbackFor("https://insecure.example/x"));
        Assert.Null(https.Upgrade("http://insecure.example/y"));
    }

    [Fact]
    public void Fallback_OnlyForUrlsThatWereUpgraded()
    {
        var https = new HttpsFirst();
        https.Upgrade("http://upgraded.example/");
        Assert.Null(https.FallbackFor("https://never-upgraded.example/"));
        Assert.Equal("http://upgraded.example/", https.FallbackFor("https://upgraded.example/"));
    }

    [Theory]
    [InlineData(-102, true)]
    [InlineData(-105, true)]
    [InlineData(-118, true)]
    [InlineData(-200, true)]
    [InlineData(-3, false)]
    [InlineData(-6, false)]
    public void ShouldFallback_OnConnectionAndCertificateErrors(int errorCode, bool expected)
        => Assert.Equal(expected, HttpsFirst.IsUpgradeFailure(errorCode));
}
