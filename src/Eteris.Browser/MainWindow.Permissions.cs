using System.Windows;
using System.Windows.Documents;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Permissions;

namespace Eteris.Browser;

public partial class MainWindow
{
    private sealed record PendingPermission(string Site, IReadOnlyCollection<SitePermission> Kinds, Action<bool> Respond);

    private readonly Queue<PendingPermission> _permissionQueue = new();
    private PendingPermission? _currentPermission;
    private SitePermissionStore? _sitePermissions;
    private SitePermissionHandler? _permissionHandler;

    private SitePermissionStore SitePermissions => _sitePermissions ??= new SitePermissionStore(AppPaths.Database);

    private SitePermissionHandler PermissionHandler => _permissionHandler ??= new SitePermissionHandler(SitePermissions,
        (site, kinds, respond) =>
        {
            if (PermissionRules.AutoDeny(kinds, Data.Settings.Current.BlockNotificationPrompts))
            {
                respond(false);
                return;
            }
            Dispatcher.InvokeAsync(() =>
        {
            _permissionQueue.Enqueue(new PendingPermission(site, kinds, respond));
            ShowNextPermission();
        });
        });

    private void ShowNextPermission()
    {
        if (_currentPermission is not null) return;
        if (!_permissionQueue.TryDequeue(out var next))
        {
            PermissionBar.Visibility = Visibility.Collapsed;
            return;
        }

        _currentPermission = next;
        PermissionText.Inlines.Clear();
        PermissionText.Inlines.Add(new Bold(new Run(next.Site)));
        PermissionText.Inlines.Add(new Run(" quer " + PermissionText_Describe(next.Kinds)));
        PermissionGlyph.Text = next.Kinds.Order().FirstOrDefault() switch
        {
            SitePermission.Camera => "",
            SitePermission.Microphone => "",
            SitePermission.ScreenShare => "",
            SitePermission.Location => "",
            SitePermission.Notifications => "",
            SitePermission.Clipboard => "",
            _ => "",
        };
        PermissionRemember.IsChecked = true;
        PermissionBar.Visibility = Visibility.Visible;
    }

    private static string PermissionText_Describe(IReadOnlyCollection<SitePermission> kinds)
        => Permissions.PermissionText.Describe(kinds) + ".";

    private void AnswerPermission(bool allowed)
    {
        if (_currentPermission is not { } current) return;
        _currentPermission = null;
        if (PermissionRemember.IsChecked == true)
            foreach (var kind in current.Kinds) SitePermissions.Set(current.Site, kind, allowed);
        current.Respond(allowed);
        ShowNextPermission();
    }

    private void PermissionAllow_Click(object sender, RoutedEventArgs e) => AnswerPermission(true);

    private void PermissionDeny_Click(object sender, RoutedEventArgs e) => AnswerPermission(false);
}
