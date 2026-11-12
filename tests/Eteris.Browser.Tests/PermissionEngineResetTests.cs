using System.IO;
using CefSharp;
using Eteris.Browser.Permissions;

namespace Eteris.Browser.Tests;

// MOT-03: aceitar ou recusar um pedido grava a decisão nas definições do próprio Chromium, para sempre. O Éteris repõe
// no motor o que a sua loja não tem, por isso esquecer uma permissão faz o site voltar mesmo a perguntar.
public class PermissionEngineResetTests
{
    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]));

    [Fact]
    public void EveryPromptKind_HasEngineTypes()
    {
        var all = PermissionRequestType.CameraStream | PermissionRequestType.MicStream | PermissionRequestType.Geolocation
                  | PermissionRequestType.Notifications | PermissionRequestType.Clipboard | PermissionRequestType.MultipleDownloads
                  | PermissionRequestType.LocalNetworkAccess;
        foreach (var kind in SitePermissionHandler.FromPrompt(all).Where(k => k != SitePermission.Clipboard))
            Assert.NotEmpty(EnginePermissions.Types[kind]);
        Assert.False(EnginePermissions.Types.ContainsKey(SitePermission.ScreenShare));
    }

    // Na CEF 156 a área de transferência (CLIPBOARD_READ_WRITE 52, CLIPBOARD_SANITIZED_WRITE 53) não passa pela API das
    // definições de conteúdo: ler ou escrever esses tipos faz cair o motor (verificação interna do Chromium, provado ao
    // vivo). Ficam de fora até haver outra via (a parte do MOT-03 sobre a área de transferência continua em aberto).
    [Fact]
    public void Types_NeverTouchTheClipboard_TheEngineCrashes()
    {
        Assert.False(EnginePermissions.Types.ContainsKey(SitePermission.Clipboard));
        Assert.DoesNotContain(EnginePermissions.Types.Values.SelectMany(t => t), n => n is 52 or 53);
    }

    // Prova ao vivo: repor as notificações logo a seguir a «Permitir» sem «Lembrar» devolvia a página a «default» 2 s
    // depois (o uso em curso era cortado). Para esse tipo, e só quando a resposta foi «permitido», a reposição fica
    // para o commit seguinte da página.
    [Fact]
    public void AfterAnswer_AnAllowedNotification_IsLeftForTheNextCommit()
    {
        bool? Nothing(SitePermission _) => null;
        Assert.Empty(EnginePermissions.AfterAnswer([SitePermission.Notifications], allowed: true, Nothing));
        Assert.Equal([SitePermission.Notifications], EnginePermissions.AfterAnswer([SitePermission.Notifications], allowed: false, Nothing));
        Assert.Equal([SitePermission.Location], EnginePermissions.AfterAnswer([SitePermission.Location], allowed: true, Nothing));
        Assert.Equal([SitePermission.LocalNetwork], EnginePermissions.AfterAnswer([SitePermission.LocalNetwork], allowed: true, Nothing));
        // O que a loja do Éteris guarda fica no motor.
        Assert.Empty(EnginePermissions.AfterAnswer([SitePermission.Location], allowed: true, _ => true));
        Assert.Empty(EnginePermissions.AfterAnswer([SitePermission.Notifications], allowed: false, _ => false));
    }

    [Fact]
    public void TypeNumbers_AreTheOnesOfCef156()
    {
        Assert.Equal(new[] { 9 }, EnginePermissions.Types[SitePermission.Camera]);
        Assert.Equal(new[] { 8 }, EnginePermissions.Types[SitePermission.Microphone]);
        Assert.Equal(new[] { 4 }, EnginePermissions.Types[SitePermission.Location]);
        Assert.Equal(new[] { 5 }, EnginePermissions.Types[SitePermission.Notifications]);
        Assert.Equal(new[] { 12 }, EnginePermissions.Types[SitePermission.MultipleDownloads]);
        Assert.Equal(new[] { 134, 142, 143 }, EnginePermissions.Types[SitePermission.LocalNetwork]);
        Assert.Equal("156.", EnginePermissions.EngineMajor);
        Assert.Contains("Include=\"CefSharp.Wpf.NETCore\" Version=\"156.", Source("Eteris.Browser.csproj"));
    }

    [Fact]
    public void Grants_AreByKindAndContext()
    {
        PermissionGrants.Allow("mot03-a.example", SitePermission.Clipboard, isPrivate: false);
        Assert.True(PermissionGrants.IsAllowed("mot03-a.example", SitePermission.Clipboard, false));
        Assert.False(PermissionGrants.IsAllowed("mot03-a.example", SitePermission.Location, false));
        Assert.False(PermissionGrants.IsAllowed("mot03-a.example", SitePermission.Clipboard, true));
        PermissionGrants.Forget("mot03-a.example", SitePermission.Clipboard);
        Assert.False(PermissionGrants.IsAllowed("mot03-a.example", SitePermission.Clipboard, false));
        // Câmara, microfone, ecrã e localização perguntam sempre, como hoje: um «Permitir» sem «Lembrar» vale uma vez.
        foreach (var kind in new[] { SitePermission.Camera, SitePermission.Microphone, SitePermission.ScreenShare, SitePermission.Location, SitePermission.LocalNetwork })
        {
            PermissionGrants.Allow("mot03-b.example", kind, isPrivate: false);
            Assert.False(PermissionGrants.IsAllowed("mot03-b.example", kind, false));
        }
    }

    [Fact]
    public void ForgetAndShred_ResetTheEngine()
    {
        Assert.Contains("EnginePermissions.Reset(CookieContext, origin, [kind]);", Source("MainWindow.SiteInfo.cs"));
        Assert.DoesNotContain("if (kind == SitePermission.LocalNetwork)", Source("MainWindow.SiteInfo.cs"));
        Assert.Contains("EnginePermissions.ResetAll(CookieContext, origin)", Source("MainWindow.Forget.cs"));
        Assert.Contains("PermissionGrants.ForgetWhere(", Source("MainWindow.Forget.cs"));
        Assert.Contains("EnginePermissions.Reset(", Source("MainWindow.xaml.cs"));
        Assert.DoesNotContain("EngineLocalNetwork", Source("Permissions", "SitePermissionHandler.cs"));
    }
}
