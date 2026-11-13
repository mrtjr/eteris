using Eteris.Browser.Browser;
using Eteris.Browser.Permissions;

namespace Eteris.Browser.Tests;

public class SiteInfoTests
{
    [Fact]
    public void Https_IsSecure()
    {
        var info = SiteInfo.Describe("https://www.publico.pt/artigo");
        Assert.Equal(ConnectionKind.Secure, info.Kind);
        Assert.Equal("www.publico.pt", info.Host);
        Assert.Equal("Ligação segura", info.Title);
        Assert.Contains("encriptad", info.Detail);
    }

    [Fact]
    public void Http_IsNotSecure()
    {
        var info = SiteInfo.Describe("http://example.com/");
        Assert.Equal(ConnectionKind.Insecure, info.Kind);
        Assert.Equal("Ligação não segura", info.Title);
        Assert.Contains("palavras-passe", info.Detail);
    }

    [Theory]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://127.0.0.1:8765/test.html")]
    public void Loopback_IsLocal(string url)
        => Assert.Equal(ConnectionKind.Local, SiteInfo.Describe(url).Kind);

    [Fact]
    public void EterisPage_IsInternal()
    {
        var info = SiteInfo.Describe("eteris://newtab/");
        Assert.Equal(ConnectionKind.Internal, info.Kind);
        Assert.Equal("Página do Éteris", info.Title);
    }

    [Fact]
    public void File_IsLocalFile()
        => Assert.Equal(ConnectionKind.File, SiteInfo.Describe("file:///C:/x.html").Kind);

    [Fact]
    public void Origin_DropsPathAndKeepsPort()
    {
        Assert.Equal("https://www.publico.pt", SiteInfo.Describe("https://www.publico.pt/a?b=1").Origin);
        Assert.Equal("http://localhost:5000", SiteInfo.Describe("http://localhost:5000/x").Origin);
    }

    [Theory]
    [InlineData(SitePermission.Camera, "Câmara")]
    [InlineData(SitePermission.Microphone, "Microfone")]
    [InlineData(SitePermission.Location, "Localização")]
    [InlineData(SitePermission.Notifications, "Notificações")]
    public void PermissionName_IsShortLabel(SitePermission kind, string name)
        => Assert.Equal(name, PermissionText.Name(kind));

    [Fact]
    public void ErrorPage_IsNeverSecure()
    {
        var info = SiteInfo.Describe("https://expirado.example/", failed: true);
        Assert.NotEqual(ConnectionKind.Secure, info.Kind);
        Assert.Equal("Não foi possível abrir este site", info.Title);
        Assert.DoesNotContain("encriptadas", info.Detail);
        Assert.Equal("expirado.example", info.Host);
    }
}
