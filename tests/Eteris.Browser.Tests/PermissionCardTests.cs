using System.IO;
using Eteris.Browser.Permissions;

namespace Eteris.Browser.Tests;

// O cartão de permissão espera 500 ms antes de aceitar um clique e pertence à aba que pediu (MOT-05).
public sealed class PermissionCardTests
{
    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Clicks_AreIgnoredInTheFirst500ms()
    {
        Assert.False(PermissionCard.AcceptsClick(T0, T0.AddMinutes(-1), T0.AddMilliseconds(499)));
        Assert.True(PermissionCard.AcceptsClick(T0, T0.AddMinutes(-1), T0.AddMilliseconds(500)));
        // A janela voltou a ter o foco 2 s depois de o cartão aparecer: o guarda recomeça.
        Assert.False(PermissionCard.AcceptsClick(T0, T0.AddSeconds(2), T0.AddMilliseconds(2300)));
        Assert.True(PermissionCard.AcceptsClick(T0, T0.AddSeconds(2), T0.AddMilliseconds(2500)));
    }

    [Fact] public void Remember_StartsOffForCameraMicrophoneScreenAndLocation()
    {
        foreach (var kind in new[] { SitePermission.Camera, SitePermission.Microphone, SitePermission.ScreenShare, SitePermission.Location })
            Assert.False(PermissionCard.RemembersByDefault([kind, SitePermission.Clipboard]));
        Assert.True(PermissionCard.RemembersByDefault([SitePermission.Notifications]));
        Assert.True(PermissionCard.RemembersByDefault([SitePermission.Clipboard, SitePermission.MultipleDownloads]));
    }

    [Fact] public void Card_ShowsOnlyForATabOnScreen()
    {
        Assert.Equal(1, PermissionCard.Next([false, true, true]));
        Assert.Equal(-1, PermissionCard.Next([false]));
        Assert.Equal(-1, PermissionCard.Next([]));
    }

    [Fact] public void Card_Leaves_WhenTheTabClosesOrNavigates()
    {
        Assert.Contains("DropPermissions(", Source("MainWindow.xaml.cs"));          // a página navegou
        Assert.Contains("DropPermissions(", Source("MainWindow.Sleep.cs"));         // o motor da aba saiu (fechar, adormecer, trocar de motor)
        Assert.Contains("DropPermissions(", Source("MainWindow.VideoEngine.cs"));
        Assert.Contains("override void OnDismissPermissionPrompt", Source("Permissions", "SitePermissionHandler.cs"));
        Assert.Equal(3, Source("MainWindow.Permissions.cs").Split("if (!PermissionCard.AcceptsClick(").Length - 1);   // os três botões
        Assert.Contains("Application.Current.Activated", Source("MainWindow.Forward.cs"));
    }

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]));
}
