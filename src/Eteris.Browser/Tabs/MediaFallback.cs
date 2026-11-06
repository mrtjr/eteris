namespace Eteris.Browser.Tabs;

// Vídeos em formatos patenteados (H.264, AAC, HEVC) não vêm no motor do Éteris, por razões de licença.
// Quando uma página precisa deles, o Éteris propõe abri-la no navegador do sistema (que tem essas licenças).
public static class MediaFallback
{
    public const string ReportPrefix = "\u0001eteris-media ";

    // Corre no início de cada documento. Avisa (uma vez) quando:
    //  - um <video>/<audio> falha por formato não suportado (MediaError 4), ou
    //  - a página perguntou por um formato patenteado e ouviu «não», nunca chegou a ligar vídeo nenhum
    //    (addSourceBuffer) e, 10 s depois, tem um vídeo visível ainda sem fonte. É o caso do leitor do Twitch,
    //    que desiste sem dar erro. O YouTube liga o vídeo em AV1/VP9 e não é sinalizado.
    public const string WatchScript = """
        (() => {
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

    public static string? ParseReport(string? message)
        => message is not null && message.StartsWith(ReportPrefix, StringComparison.Ordinal)
           && message[ReportPrefix.Length..] is "format" or "patented-only" ? message[ReportPrefix.Length..] : null;

    // O que pedir ao Windows para abrir o endereço. Se o navegador do sistema for o próprio Éteris, abre-se o Edge
    // (vem sempre com o Windows e tem as licenças dos formatos), para a página não voltar para aqui.
    public static string SystemBrowserTarget(string? defaultBrowserProgId, string url)
        => defaultBrowserProgId is null || defaultBrowserProgId.Contains("Eteris", StringComparison.OrdinalIgnoreCase)
            ? "microsoft-edge:" + url
            : url;

    public static string? DefaultBrowserProgId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return key?.GetValue("ProgId") as string;
        }
        catch (Exception) { return null; }
    }
}
