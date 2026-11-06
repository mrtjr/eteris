using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class SecureDnsTests
{
    [Theory]
    [InlineData("cloudflare", "https://chrome.cloudflare-dns.com/dns-query")]
    [InlineData("quad9", "https://dns.quad9.net/dns-query")]
    [InlineData("mullvad", "https://dns.mullvad.net/dns-query")]
    public void Providers_HaveDohTemplates(string id, string template)
        => Assert.Equal(template, SecureDns.TemplateFor(id));

    [Fact]
    public void Off_HasNoTemplate()
        => Assert.Null(SecureDns.TemplateFor("off"));

    [Theory]
    [InlineData("off", "off")]
    [InlineData("quad9", "secure")]
    public void Mode_IsSecureWhenAProviderIsChosen(string id, string mode)
        => Assert.Equal(mode, SecureDns.ModeFor(id));

    [Fact]
    public void Setting_DefaultsOffAndValidates()
    {
        Assert.Equal("off", new UserSettings().SecureDns);
        Assert.NotNull(new UserSettings().With("dns", "mullvad"));
        Assert.Null(new UserSettings().With("dns", "8.8.8.8"));
    }
}
