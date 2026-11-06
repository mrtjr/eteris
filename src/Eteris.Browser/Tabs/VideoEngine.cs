using Microsoft.Web.WebView2.Core;

namespace Eteris.Browser.Tabs;

// Motor de vídeo: o motor principal do Éteris não traz os formatos patenteados H.264/AAC (licenças de patentes).
// Para os sites que só os usam (o Twitch, por exemplo), a aba passa a usar o WebView2 — o motor do Edge que vem
// com o Windows e já tem essas licenças —, dentro do próprio Éteris. O site fica lembrado.
public static class VideoEngine
{
    public const string ReportPrefix = "\u0001eteris-media ";

    // Corre no início de cada documento do motor principal. Avisa (uma vez) quando:
    //  - um <video>/<audio> falha por formato não suportado (MediaError 4), ou
    //  - a página perguntou por um formato patenteado e ouviu «não», nunca ligou vídeo nenhum (addSourceBuffer)
    //    e, 10 s depois, tem um vídeo visível ainda sem fonte — o leitor do Twitch desiste assim, sem dar erro.
    // O YouTube liga o vídeo em AV1/VP9 e não é sinalizado.
    public const string WatchScript = """
        (() => {
          // Só a página principal: um vídeo incorporado noutro site não muda a aba toda de motor.
          if (window.top !== window) return;
          const patented = /avc1|avc3|mp4a|hvc1|hev1|h264|aac/i;
          let rejected = false, attached = false, reported = false;
          const report = reason => { if (!reported) { reported = true; console.debug('\u0001eteris-media ' + reason); } };
          if (window.MediaSource) {
            const isTypeSupported = MediaSource.isTypeSupported;
            MediaSource.isTypeSupported = function (type) {
              const supported = isTypeSupported.apply(this, arguments);
              if (!supported && typeof type === 'string' && patented.test(type)) rejected = true;
              return supported;
            };
            const addSourceBuffer = MediaSource.prototype.addSourceBuffer;
            MediaSource.prototype.addSourceBuffer = function () {
              const buffer = addSourceBuffer.apply(this, arguments);
              attached = true;
              return buffer;
            };
          }
          addEventListener('error', e => {
            const media = e.target;
            if (media instanceof HTMLMediaElement && media.error && media.error.code === 4) report('format');
          }, true);
          setTimeout(() => {
            if (!rejected || attached) return;
            const stuck = [...document.querySelectorAll('video')].some(v => {
              const box = v.getBoundingClientRect();
              return box.width >= 320 && box.height >= 180 && v.readyState === 0 && !v.currentSrc && !v.srcObject;
            });
            if (stuck) report('patented-only');
          }, 10000);
        })();
        """;

    public static bool IsReport(string? message)
        => message is not null && message.StartsWith(ReportPrefix, StringComparison.Ordinal)
           && message[ReportPrefix.Length..] is "format" or "patented-only";

    // Argumentos do motor do Edge: sem SmartScreen (enviaria os endereços visitados à Microsoft), sem a interface
    // do Office nos PDFs, sem tráfego de fundo nem pings de auditoria de links.
    public const string BrowserArguments =
        "--disable-features=msSmartScreenProtection,msWebOOUI,msPdfOOUI --disable-background-networking --no-pings";

    // Só os endereços web passam para o motor de vídeo; as páginas internas (eteris://) ficam no motor principal.
    public static bool CanHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // Tipos de pedido do WebView2 com os nomes que as listas de filtros usam ($script, $image…).
    public static string ToAdblock(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Document => "document",
        CoreWebView2WebResourceContext.Script => "script",
        CoreWebView2WebResourceContext.Stylesheet => "stylesheet",
        CoreWebView2WebResourceContext.Image => "image",
        CoreWebView2WebResourceContext.Font => "font",
        CoreWebView2WebResourceContext.Media => "media",
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch => "xmlhttprequest",
        CoreWebView2WebResourceContext.Ping => "ping",
        CoreWebView2WebResourceContext.Websocket => "websocket",
        _ => "other",
    };
}
