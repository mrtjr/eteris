namespace Eteris.Browser.Tabs;

// Vídeos em formatos patenteados (H.264, AAC, HEVC) não vêm no motor do Éteris, por razões de licença.
// Quando uma página precisa deles, o Éteris propõe abri-la no navegador do sistema (que tem essas licenças).
public static class MediaFallback
{
    public const string ReportPrefix = "\u0001eteris-media ";

    // Corre no início de cada documento. Avisa (uma vez) quando:
    //  - um <video>/<audio> falha por formato não suportado (MediaError 4), ou
    //  - a página só perguntou por formatos patenteados (nunca por VP8/VP9/AV1/Opus) e tem um vídeo:
    //    é o caso de leitores como o do Twitch, que desistem sem chegar a dar erro.
    // O YouTube pergunta também por VP9/AV1 e não é sinalizado.
    public const string WatchScript = """
        (() => {
          const patented = /avc1|avc3|mp4a|hvc1|hev1|h264|aac/i, free = /vp8|vp9|vp09|av01|opus|vorbis|flac/i;
          let askedPatented = false, askedFree = false, reported = false;
          const report = reason => { if (!reported) { reported = true; console.debug('\u0001eteris-media ' + reason); } };
          const note = type => {
            if (typeof type !== 'string') return;
            if (patented.test(type)) askedPatented = true;
            if (free.test(type)) askedFree = true;
          };
          const watch = (target, name) => {
            const original = target && target[name];
            if (typeof original !== 'function') return;
            target[name] = function (type) { note(type); return original.apply(this, arguments); };
          };
          if (window.MediaSource) watch(MediaSource, 'isTypeSupported');
          if (window.HTMLMediaElement) watch(HTMLMediaElement.prototype, 'canPlayType');
          addEventListener('error', e => {
            const media = e.target;
            if (media instanceof HTMLMediaElement && media.error && media.error.code === 4) report('format');
          }, true);
          setTimeout(() => { if (askedPatented && !askedFree && document.querySelector('video')) report('patented-only'); }, 6000);
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
