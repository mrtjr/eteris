using System.Windows;
using System.Windows.Documents;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;

namespace Eteris.Browser;

public partial class MainWindow
{
    private sealed record PendingPermission(string Site, IReadOnlyCollection<SitePermission> Kinds, Action<bool> Respond, string? Detail = null)
    {
        public bool IsLocalNetwork => Kinds.Count > 0 && Kinds.All(k => k == SitePermission.LocalNetwork);
    }

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
            // O pedido do próprio Chromium para a rede local (WebSockets, nomes que apontam para endereços locais):
            // vale o que o utilizador já deixou, incluindo «só desta vez».
            if (kinds.Count > 0 && kinds.All(k => k == SitePermission.LocalNetwork) && LocalNetworkGrants.IsAllowed(site, IsPrivate))
            {
                respond(true);
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
        // Rede local: «Bloquear», «Só desta vez» e «Permitir sempre» (na janela privada nada fica guardado: sem «sempre»).
        var lan = next.IsLocalNetwork;
        PermissionDetail.Text = next.Detail ?? string.Empty;
        PermissionDetail.Visibility = string.IsNullOrEmpty(next.Detail) ? Visibility.Collapsed : Visibility.Visible;
        PermissionRemember.Visibility = PermissionRememberLabel.Visibility = lan ? Visibility.Collapsed : Visibility.Visible;
        PermissionOnceButton.Visibility = lan ? Visibility.Visible : Visibility.Collapsed;
        PermissionAllowButton.Visibility = lan && IsPrivate ? Visibility.Collapsed : Visibility.Visible;
        PermissionAllowButton.Content = lan ? "Permitir sempre" : "Permitir";
        PermissionGlyph.Text = lan ? "" : next.Kinds.Order().FirstOrDefault() switch
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

    private void AnswerPermission(bool allowed, bool once = false)
    {
        if (_currentPermission is not { } current) return;
        _currentPermission = null;
        if (current.IsLocalNetwork)
        {
            // «Bloquear» não fica guardado (o cartão não volta a aparecer nesta janela para o site); «Só desta vez» vale
            // até fechar o Éteris; «Permitir sempre» fica nas permissões do site (e aparece no cadeado).
            if (allowed && (once || IsPrivate)) LocalNetworkGrants.Allow(current.Site, IsPrivate);
            else if (allowed) SitePermissions.Set(current.Site, SitePermission.LocalNetwork, true);
        }
        else if (PermissionRemember.IsChecked == true)
            foreach (var kind in current.Kinds) SitePermissions.Set(current.Site, kind, allowed);
        current.Respond(allowed);
        ShowNextPermission();
    }

    private void PermissionAllow_Click(object sender, RoutedEventArgs e) => AnswerPermission(true);

    private void PermissionOnce_Click(object sender, RoutedEventArgs e) => AnswerPermission(true, once: true);

    private void PermissionDeny_Click(object sender, RoutedEventArgs e) => AnswerPermission(false);
}
