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
        var context = browser.GetHost()?.RequestContext;
        Resolve(requestingOrigin, kinds, allowed =>
        {
            using (callback) callback.Continue(allowed ? PermissionRequestResult.Accept : PermissionRequestResult.Deny);
            // Aceitar ou recusar o pedido grava ALLOW/BLOCK nas definições do próprio Chromium, para sempre. Para a rede
            // local só vale o que o Éteris guardou («Permitir sempre»): «Só desta vez» e «Bloquear» não ficam no motor.
            if (kinds.Contains(SitePermission.LocalNetwork) && context is not null
                && store.Get(PermissionRules.Site(requestingOrigin), SitePermission.LocalNetwork) != true)
                EngineLocalNetwork.Reset(context, requestingOrigin);
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

// As decisões do Chromium sobre a rede local (Local Network Access) de uma origem voltam ao valor por omissão (perguntar,
// o que cai no cartão do Éteris): depois de «Só desta vez»/«Bloquear», ao triturar ou esquecer o site e quando o
// cadeado esquece «Rede local».
public static class EngineLocalNetwork
{
    // O enum do CefSharp 156 não tem estes tipos e os números dele já não batem com os da CEF: o CefSharp passa o
    // número tal e qual à CEF, por isso usam-se os de cef_types_content_settings.h da CEF 156 (LOCAL_NETWORK_ACCESS,
    // LOCAL_NETWORK e LOOPBACK_NETWORK). Só para o motor 156: noutro, os números podem ter mudado e nada se toca.
    private static readonly CefSharp.Enums.ContentSettingTypes[] Types =
        [(CefSharp.Enums.ContentSettingTypes)134, (CefSharp.Enums.ContentSettingTypes)142, (CefSharp.Enums.ContentSettingTypes)143];

    public const string EngineMajor = "156.";

    public static void Reset(IRequestContext context, string origin)
    {
        if (!Cef.ChromiumVersion.StartsWith(EngineMajor, StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        var url = uri.GetLeftPart(UriPartial.Authority) + "/";
        try
        {
            // As definições de conteúdo mexem-se na thread de interface do CEF (como o próprio pedido).
            _ = Cef.UIThreadTaskFactory.StartNew(() =>
            {
                foreach (var type in Types)
                {
                    try
                    {
                        if (context.GetContentSetting(url, url, type) != CefSharp.Enums.ContentSettingValues.Default)
                            context.SetContentSetting(url, url, type, CefSharp.Enums.ContentSettingValues.Default);
                    }
                    catch (Exception) { }
                }
            });
        }
        catch (Exception) { }
    }
}
