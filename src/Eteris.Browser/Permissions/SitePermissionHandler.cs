using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Permissions;

// Um pedido que o Éteris tem de perguntar ao utilizador. Respond e Drop partilham uma guarda (PermissionAsk.Guard): só a
// primeira chamada, de qualquer dos dois, chega ao callback do motor. Drop larga o pedido sem resposta do utilizador
// (Dismiss num pedido, Cancel na multimédia). Source é a página que pede (para saber a aba); EmbeddedIn, o site da
// página de topo quando o pedido vem de uma moldura de outro site (null: é a própria página); PromptId identifica o
// pedido que o Chromium pode retirar (OnDismissPermissionPrompt).
public sealed record PermissionAsk(string Site, IReadOnlyCollection<SitePermission> Kinds, Action<bool> Respond, Action Drop,
    CefSharp.IWebBrowser? Source = null, string? EmbeddedIn = null, ulong? PromptId = null)
{
    // O motor da aba pode já ter saído quando se responde (a aba fechou, a página navegou): nunca rebenta, e só a primeira
    // resposta conta.
    public static (Action<bool> Respond, Action Drop) Guard(Action<bool> respond, Action drop)
    {
        var answered = 0;
        return (allowed =>
        {
            if (Interlocked.Exchange(ref answered, 1) != 0) return;
            try { respond(allowed); } catch (Exception) { }
        }, () =>
        {
            if (Interlocked.Exchange(ref answered, 1) != 0) return;
            try { drop(); } catch (Exception) { }
        });
    }
}

// Pedidos de permissão: decisões lembradas por site; o resto é perguntado na barra do Éteris.
public sealed class SitePermissionHandler(SitePermissionStore store, Action<PermissionAsk> ask, Action<CefSharp.IWebBrowser>? onCapture = null)
    : CefSharp.Handler.PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId,
        string requestingOrigin, PermissionRequestType permissionRequestTypes, IPermissionPromptCallback callback)
    {
        var kinds = FromPrompt(permissionRequestTypes);
        var context = browser.GetHost()?.RequestContext;
        var (respond, drop) = PermissionAsk.Guard(allowed =>
        {
            using (callback) callback.Continue(allowed ? PermissionRequestResult.Accept : PermissionRequestResult.Deny);
            // Aceitar ou recusar o pedido grava ALLOW/BLOCK nas definições do próprio Chromium, para sempre. O motor só
            // fica com a decisão que também está na loja do Éteris («Lembrar», «Permitir sempre»): o resto volta ao
            // valor por omissão (perguntar, o que cai no cartão do Éteris). Vale também para a recusa automática das
            // notificações, que responde por aqui. Uma notificação permitida fica para o commit seguinte (AfterAnswer).
            if (context is not null)
                EnginePermissions.Reset(context, requestingOrigin,
                    EnginePermissions.AfterAnswer(kinds, allowed, k => store.Get(PermissionRules.Site(requestingOrigin), k)));
        }, () =>
        {
            using (callback) callback.Continue(PermissionRequestResult.Dismiss);
        });
        // O Chromium já decide estes pedidos pela página de topo: EmbeddedIn fica null.
        Resolve(requestingOrigin, kinds, embeddedIn: null, new PermissionAsk(PermissionRules.Site(requestingOrigin), kinds, respond, drop,
            Source: chromiumWebBrowser, EmbeddedIn: null, PromptId: promptId));
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

        var browserId = browser.Identifier;
        // Quem pede é a origem da moldura; a página de topo pode ser outra (uma moldura de outro site): a decisão
        // lembrada para o site da moldura não vale então, e o cartão diz onde está embutida.
        var embeddedIn = PermissionRules.EmbeddedIn(SafeFrame.MainUrl(browser), requestingOrigin);
        var (respond, drop) = PermissionAsk.Guard(allowed =>
        {
            // Com a câmara, o microfone ou o ecrã a ser usados, a página nunca se esconde com a janela (ajuste e8).
            if (allowed) Tabs.CaptureSessions.Started(browserId);
            if (allowed) onCapture?.Invoke(chromiumWebBrowser);
            using (callback)
            {
                if (allowed) callback.Continue(requestedPermissions);
                else callback.Cancel();
            }
        }, () =>
        {
            using (callback) callback.Cancel();
        });
        Resolve(requestingOrigin, kinds, embeddedIn, new PermissionAsk(PermissionRules.Site(requestingOrigin), kinds, respond, drop,
            Source: chromiumWebBrowser, EmbeddedIn: embeddedIn, PromptId: null));
        return true;
    }

    private void Resolve(string origin, IReadOnlyCollection<SitePermission> kinds, string? embeddedIn, PermissionAsk request)
    {
        var site = PermissionRules.Site(origin);
        switch (PermissionRules.Decide(store, site, kinds, embedded: embeddedIn is not null))
        {
            case PermissionDecision.Allow: request.Respond(true); break;
            case PermissionDecision.Deny: request.Respond(false); break;
            default: ask(request); break;
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

// As decisões que o Chromium guardou para uma origem voltam ao valor por omissão (perguntar, o que cai no cartão do
// Éteris): depois de cada resposta que o Éteris não guardou, ao esquecer no cadeado, ao triturar ou esquecer o site e no
// commit de cada página (restos de versões anteriores: o motor não deixa enumerar as decisões guardadas). MOT-03.
public static class EnginePermissions
{
    // Os números são os de cef_types_content_settings.h da CEF 156 (GEOLOCATION 4, NOTIFICATIONS 5, MEDIASTREAM_MIC 8,
    // MEDIASTREAM_CAMERA 9, AUTOMATIC_DOWNLOADS 12, LOCAL_NETWORK_ACCESS 134, LOCAL_NETWORK 142, LOOPBACK_NETWORK 143):
    // o enum do CefSharp 156 não tem alguns destes tipos e os números dele já não batem com os da CEF, e o CefSharp passa
    // o número tal e qual à CEF. Só para o motor 156: noutro, os números podem ter mudado e nada se toca. A partilha do
    // ecrã não tem definição guardada. A área de transferência (CLIPBOARD_READ_WRITE 52, CLIPBOARD_SANITIZED_WRITE 53)
    // fica de fora: na CEF 156 ler ou escrever esses tipos por esta API faz cair o motor (verificação interna do
    // Chromium, provado ao vivo); a decisão dela continua guardada no motor até haver outra via.
    public const string EngineMajor = "156.";

    public static IReadOnlyDictionary<SitePermission, int[]> Types { get; } = new Dictionary<SitePermission, int[]>
    {
        [SitePermission.Camera] = [9],
        [SitePermission.Microphone] = [8],
        [SitePermission.Location] = [4],
        [SitePermission.Notifications] = [5],
        [SitePermission.MultipleDownloads] = [12],
        [SitePermission.LocalNetwork] = [134, 142, 143],
    };

    public static void ResetAll(IRequestContext context, string origin) => Reset(context, origin, Types.Keys.ToList());

    // O que se repõe no commit de uma página: os tipos que a loja do Éteris não guarda para o site. Corre em cada
    // navegação, num evento do CEF: se a loja não se ler (base ocupada, loja da janela privada já fechada), não se repõe
    // nada — uma exceção ali saltava o resto do tratador ou fazia cair o processo.
    public static List<SitePermission> Unstored(Func<SitePermission, bool?> stored)
    {
        try { return Types.Keys.Where(k => stored(k) is null).ToList(); }
        catch (Exception) { return []; }
    }

    // O que se repõe logo a seguir a uma resposta: o que a loja do Éteris não guarda. Uma notificação permitida fica para
    // o commit seguinte da página: repô-la já devolvia a página a «default» 2 s depois (prova ao vivo), cortando o uso
    // que acabou de ser permitido.
    public static List<SitePermission> AfterAnswer(IEnumerable<SitePermission> kinds, bool allowed, Func<SitePermission, bool?> stored)
        => kinds.Where(k => stored(k) is null && !(allowed && k == SitePermission.Notifications)).ToList();

    public static void Reset(IRequestContext context, string origin, IEnumerable<SitePermission> kinds)
    {
        var types = kinds.Where(Types.ContainsKey).SelectMany(k => Types[k]).Distinct()
            .Select(n => (CefSharp.Enums.ContentSettingTypes)n).ToList();
        if (types.Count == 0) return;
        if (!Cef.ChromiumVersion.StartsWith(EngineMajor, StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        var url = uri.GetLeftPart(UriPartial.Authority) + "/";
        try
        {
            // As definições de conteúdo mexem-se na thread de interface do CEF (como o próprio pedido).
            _ = Cef.UIThreadTaskFactory.StartNew(() =>
            {
                foreach (var type in types)
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
