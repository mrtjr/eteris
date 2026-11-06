using CefSharp;

namespace Eteris.Browser.Shields;

// Preferências do Chromium aplicadas ao perfil inteiro; os resultados aparecem no painel do escudo.
public static class PrivacyPreferences
{
    public static bool ThirdPartyCookiesBlocked { get; private set; }

    public static bool WebRtcIpProtected { get; private set; }

    public static Task ApplyAsync() => Cef.UIThreadTaskFactory.StartNew(() =>
    {
        var context = Cef.GetGlobalRequestContext();
        ThirdPartyCookiesBlocked =
            context.SetPreference("profile.block_third_party_cookies", true, out _)
            | context.SetPreference("profile.cookie_controls_mode", 1, out _);
        WebRtcIpProtected = context.SetPreference("webrtc.ip_handling_policy", "disable_non_proxied_udp", out _);
    });
}
