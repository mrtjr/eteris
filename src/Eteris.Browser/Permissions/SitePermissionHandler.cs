using CefSharp;

namespace Eteris.Browser.Permissions;

// Pedidos de permissão: decisões lembradas por site; o resto é perguntado na barra do Éteris.
public sealed class SitePermissionHandler(SitePermissionStore store, Action<string, IReadOnlyCollection<SitePermission>, Action<bool>> ask)
    : CefSharp.Handler.PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId,
        string requestingOrigin, PermissionRequestType permissionRequestTypes, IPermissionPromptCallback callback)
    {
        var kinds = FromPrompt(permissionRequestTypes);
        Resolve(requestingOrigin, kinds, allowed =>
        {
            using (callback) callback.Continue(allowed ? PermissionRequestResult.Accept : PermissionRequestResult.Deny);
        });
        return true;
    }

    protected override bool OnRequestMediaAccessPermission(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        string requestingOrigin, MediaAccessPermissionType requestedPermissions, IMediaAccessCallback callback)
    {
        var kinds = new List<SitePermission>();
        if (requestedPermissions.HasFlag(MediaAccessPermissionType.VideoCapture)) kinds.Add(SitePermission.Camera);
        if (requestedPermissions.HasFlag(MediaAccessPermissionType.AudioCapture)) kinds.Add(SitePermission.Microphone);
        if (requestedPermissions.HasFlag(MediaAccessPermissionType.DesktopVideoCapture)
            || requestedPermissions.HasFlag(MediaAccessPermissionType.DesktopAudioCapture)) kinds.Add(SitePermission.ScreenShare);

        Resolve(requestingOrigin, kinds, allowed =>
        {
            using (callback)
            {
                if (allowed) callback.Continue(requestedPermissions);
                else callback.Cancel();
            }
        });
        return true;
    }

    private void Resolve(string origin, IReadOnlyCollection<SitePermission> kinds, Action<bool> respond)
    {
        var site = PermissionRules.Site(origin);
        switch (PermissionRules.Decide(store, site, kinds))
        {
            case PermissionDecision.Allow: respond(true); break;
            case PermissionDecision.Deny: respond(false); break;
            default: ask(site, kinds, respond); break;
        }
    }

    public static List<SitePermission> FromPrompt(PermissionRequestType types)
    {
        var kinds = new List<SitePermission>();
        if (types.HasFlag(PermissionRequestType.CameraStream)) kinds.Add(SitePermission.Camera);
        if (types.HasFlag(PermissionRequestType.MicStream)) kinds.Add(SitePermission.Microphone);
        if (types.HasFlag(PermissionRequestType.Geolocation)) kinds.Add(SitePermission.Location);
        if (types.HasFlag(PermissionRequestType.Notifications)) kinds.Add(SitePermission.Notifications);
        if (types.HasFlag(PermissionRequestType.Clipboard)) kinds.Add(SitePermission.Clipboard);
        if (types.HasFlag(PermissionRequestType.MultipleDownloads)) kinds.Add(SitePermission.MultipleDownloads);
        if ((types & (PermissionRequestType.LocalNetworkAccess | PermissionRequestType.LocalNetwork | PermissionRequestType.LoopbackNetwork)) != 0)
            kinds.Add(SitePermission.LocalNetwork);
        return kinds;
    }
}
