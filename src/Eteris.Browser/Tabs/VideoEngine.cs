using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Eteris.Browser.Tabs;

// Motor de vídeo: o motor principal do Éteris (runtime do NuGet) não traz os formatos patenteados H.264/AAC (licenças
// de patentes). Para os sites que só os usam (o Twitch, por exemplo), a aba passa a usar o WebView2 — o motor do Edge
// que vem com o Windows e já tem essas licenças —, dentro do próprio Éteris. O site fica lembrado.
// Com o motor com codecs da plataforma (Tabs/PlatformCodecs.cs), o motor principal toca-os pelo Windows e o motor de
// vídeo passa a ser só o recurso: quando o próprio motor responde «não» (sem descodificação por hardware, Windows N).
public static class VideoEngine
{
    public const string ReportPrefix = "\u0001eteris-media ";

    // Corre no início de cada documento do motor principal (também nos iframes do mesmo processo). Avisa uma vez por
    // documento, com «razão força origem» (ver VideoReport). Os sinais foram medidos site a site (docs/motor.md, V1):
    //  - MediaError 4 num <video>/<audio>, também dentro de shadow DOM (Reddit, BBC): o evento «error» não atravessa a
    //    fronteira da shadow root, por isso o ouvinte vai para o próprio elemento (play/load/src) e a verificação desce
    //    pelas shadow roots;
    //  - erro no último <source> com tipo ou extensão patenteados e o leitor em NETWORK_NO_SOURCE (MP4 aberto
    //    diretamente — o documento de multimédia do Chromium usa <source> —, w3schools): o media.error fica null;
    //  - addSourceBuffer a lançar NotSupportedError para avc1/mp4a (Vimeo, BBC): logo, sem esperar;
    //  - a página perguntou por H.264/AAC (isTypeSupported, canPlayType) e ouviu «não», e aos 3 s/8 s tem um vídeo
    //    visível parado sem fonte ou com um MediaSource vazio (blob:) — Twitch, X, g1, hls.js;
    //  - (sinal secundário, só se perguntou) uma mensagem do leitor de uma lista curta («Player error»…);
    //  - DRM: requestMediaKeySystemAccess recusado para Widevine/PlayReady → «drm» (a janela pergunta, nunca troca);
    //  - no topo: um leitor conhecido de outro site num iframe (Vimeo, Dailymotion, Brightcove…) → «frame-player».
    // HEVC/Dolby Vision (hvc1/hev1/dvh1) não contam: quase todos os leitores sondam-nos sem precisar deles (o Twitch
    // offline trocava de motor só por isso). O YouTube liga o vídeo em AV1/VP9 e não é sinalizado.
    //  - motor com codecs sem H.264 (sem descodificação por hardware): o FFmpeg descarta em silêncio a faixa de vídeo
    //    de um MP4 progressivo e toca só o som — um <video> de MP4/MOV com metadados, sem imagem (videoWidth 0), e o
    //    motor a dizer «não» ao avc1.
    // Cada aviso diz no fim («no») se o motor deste documento respondeu «não» ao H.264/AAC: à página (isTypeSupported,
    // canPlayType, addSourceBuffer) ou à pergunta do próprio script, com a função original guardada no arranque. Com
    // o motor com codecs, só esses avisos contam (Decide).
    // «strong» = vídeo relevante (visível, ≥ 200×110 ou ≥ 30 000 px², não é um fundo mudo em ciclo sem controlos) ou
    // som pedido com play(); o resto é «weak». O console.debug é guardado logo no arranque (o g1 substitui-o).
    public const string WatchScript = """
        (() => {
          const mark = Symbol.for('eteris.media');
          if (window[mark]) return;
          try { Object.defineProperty(window, mark, { value: true }); } catch (e) { return; }
          const late = typeof __eterisLate !== 'undefined';
          const debug = console.debug.bind(console);
          const inFrame = window.top !== window;
          const needed = /avc1|avc3|mp4a|h264|aac/i;
          const sourceType = /mp4|m4v|m4a|quicktime|aac|mpegurl|avc1|avc3|mp4a/i;
          const sourceExt = /\.(mp4|m4v|mov|m4a|aac|m3u8)(?:$|[?#])/i;
          const pictureType = /video\/(mp4|quicktime|x-m4v)|mpegurl|avc1|avc3/i;
          const pictureExt = /\.(mp4|m4v|mov|m3u8)(?:$|[?#])/i;
          const playerHosts = /^(player\.vimeo\.com|geo\.dailymotion\.com|www\.dailymotion\.com|players\.brightcove\.net|cdn\.jwplayer\.com|content\.jwplatform\.com|player\.twitch\.tv|clips\.twitch\.tv|fast\.wistia\.net)$/i;
          const messages = ['player error', 'manifestincompatiblecodecs', 'não é possível reproduzir este vídeo', 'navegador não compatível',
            'navegador não é compatível', 'browser is not supported', 'unsupported browser', 'format is not supported',
            'format not supported', 'formato não suportado', 'no compatible source'];
          let rejected = false, drmRejected = false, attached = false, reported = false, timer = 0;
          const tried = new WeakSet(), played = new WeakSet(), watched = new WeakSet();
          const nativeSupported = window.MediaSource && MediaSource.isTypeSupported;
          const engineSays = mime => { try { return !!nativeSupported.call(MediaSource, mime); } catch (e) { return true; } };
          const lacksVideo = () => !engineSays('video/mp4; codecs="avc1.640028"');
          const lacksAudio = () => !engineSays('audio/mp4; codecs="mp4a.40.2"');
          const report = (reason, strength) => {
            if (reported) return;
            if (drmRejected && !rejected && reason !== 'frame-player') reason = 'drm';
            reported = true;
            const said = rejected || lacksVideo() || lacksAudio() ? ' no' : '';
            try { debug('\u0001eteris-media ' + reason + ' ' + strength + ' ' + (inFrame ? 'frame' : 'top') + said); } catch (e) {}
          };
          const visible = el => {
            const b = el.getBoundingClientRect();
            const big = (b.width >= 200 && b.height >= 110) || b.width * b.height >= 30000;
            return big && b.bottom > 0 && b.right > 0 && b.top < innerHeight && b.left < innerWidth;
          };
          const strength = el => {
            if (!(el instanceof HTMLVideoElement)) return played.has(el) ? 'strong' : 'weak';
            if (el.muted && el.loop && !el.controls) return 'weak';
            return visible(el) || played.has(el) ? 'strong' : 'weak';
          };
          const strongest = list => list.some(el => strength(el) === 'strong') ? 'strong' : 'weak';
          const deepMedia = (root, out) => {
            for (const el of root.querySelectorAll('*')) {
              if (el instanceof HTMLMediaElement) out.push(el);
              if (el.shadowRoot) deepMedia(el.shadowRoot, out);
            }
            return out;
          };
          const needsPatented = media => [...media.querySelectorAll('source')].some(s =>
            sourceType.test(s.getAttribute('type') || '') || sourceExt.test(s.getAttribute('src') || ''));
          const onError = e => {
            try {
              const t = e.target;
              if (t instanceof HTMLMediaElement) {
                if (t.error && t.error.code === 4) report('format', strength(t));
              } else if (t instanceof HTMLSourceElement && t.parentElement instanceof HTMLMediaElement) {
                const media = t.parentElement;
                setTimeout(() => {
                  if (media.networkState === 3 && media.readyState === 0 && needsPatented(media)) report('format', strength(media));
                }, 50);
              }
            } catch (x) {}
          };
          const watch = el => {
            if (watched.has(el)) return;
            watched.add(el);
            el.addEventListener('error', onError, true);
          };
          const noPicture = v => v instanceof HTMLVideoElement && v.readyState >= 1 && v.videoWidth === 0 && !v.srcObject
            && !!v.currentSrc && !v.currentSrc.startsWith('blob:')
            && (pictureExt.test(v.currentSrc) || [...v.querySelectorAll('source')].some(s => pictureType.test(s.getAttribute('type') || '')))
            && lacksVideo();
          const stuck = v => v.readyState === 0 && !v.srcObject
            && (!v.currentSrc || v.currentSrc.startsWith('blob:'))
            && (v.networkState !== 0 || v.getAttribute('src') || v.autoplay || tried.has(v))
            && visible(v);
          const playerMessage = () => {
            const text = ((document.body && document.body.innerText) || '').toLowerCase();
            return messages.some(m => text.includes(m));
          };
          const check = () => {
            timer = 0;
            if (reported) return;
            try {
              const media = deepMedia(document, []);
              for (const m of media) {
                watch(m);
                if (m.error && m.error.code === 4) return report('format', strength(m));
                if (m.networkState === 3 && m.readyState === 0 && !m.getAttribute('src') && needsPatented(m)) return report('format', strength(m));
                if (noPicture(m)) return report('format', strength(m));
              }
              const videos = media.filter(v => v instanceof HTMLVideoElement);
              const parked = videos.filter(stuck);
              if (late && parked.length && !attached && !rejected) return report('patented-only', 'weak');
              if ((rejected || drmRejected) && !attached && parked.length) return report('patented-only', strongest(parked));
              if ((rejected || drmRejected) && playerMessage()) {
                const shown = videos.filter(visible);
                return report('format', shown.length ? strongest(shown) : 'weak');
              }
              if (!inFrame && !videos.some(v => !v.paused && v.readyState > 2)) {
                const frame = [...document.querySelectorAll('iframe')].some(f => {
                  try { return playerHosts.test(new URL(f.src, location.href).hostname) && visible(f); } catch (x) { return false; }
                });
                if (frame) report('frame-player', 'weak');
              }
            } catch (e) {}
          };
          const later = ms => { if (!reported && !timer) timer = setTimeout(check, ms); };
          const seen = e => {
            const t = e.target;
            if (t instanceof HTMLMediaElement) { tried.add(t); watch(t); later(3000); }
          };
          addEventListener('error', onError, true);
          addEventListener('loadstart', seen, true);
          addEventListener('play', seen, true);
          try {
            const proto = HTMLMediaElement.prototype;
            for (const name of ['play', 'load']) {
              const original = proto[name];
              proto[name] = function () {
                try { tried.add(this); if (name === 'play') played.add(this); watch(this); later(3000); } catch (e) {}
                return original.apply(this, arguments);
              };
            }
            const src = Object.getOwnPropertyDescriptor(proto, 'src');
            if (src && src.set) Object.defineProperty(proto, 'src', {
              configurable: true, enumerable: src.enumerable, get: src.get,
              set(value) { try { tried.add(this); watch(this); } catch (e) {} return src.set.call(this, value); },
            });
            const canPlayType = proto.canPlayType;
            proto.canPlayType = function (type) {
              const answer = canPlayType.apply(this, arguments);
              if (answer === '' && typeof type === 'string' && needed.test(type)) rejected = true;
              return answer;
            };
          } catch (e) {}
          for (const name of ['MediaSource', 'ManagedMediaSource', 'WebKitMediaSource']) {
            const type = window[name];
            if (!type || !Object.prototype.hasOwnProperty.call(type, 'isTypeSupported')) continue;
            const isTypeSupported = type.isTypeSupported;
            type.isTypeSupported = function (mime) {
              const supported = isTypeSupported.apply(this, arguments);
              if (!supported && typeof mime === 'string' && needed.test(mime)) rejected = true;
              return supported;
            };
          }
          if (window.MediaSource) {
            const addSourceBuffer = MediaSource.prototype.addSourceBuffer;
            MediaSource.prototype.addSourceBuffer = function (mime) {
              try {
                const buffer = addSourceBuffer.apply(this, arguments);
                attached = true;
                return buffer;
              } catch (e) {
                if (e && e.name === 'NotSupportedError' && typeof mime === 'string' && needed.test(mime)) {
                  rejected = true;
                  setTimeout(() => {
                    try {
                      const mse = deepMedia(document, []).filter(v => v instanceof HTMLVideoElement && (v.currentSrc || '').startsWith('blob:'));
                      report('format', mse.length ? strongest(mse) : 'weak');
                    } catch (x) {}
                  }, 0);
                }
                throw e;
              }
            };
          }
          if (navigator.requestMediaKeySystemAccess) {
            const request = navigator.requestMediaKeySystemAccess;
            navigator.requestMediaKeySystemAccess = function (keySystem) {
              const answer = request.apply(this, arguments);
              if (typeof keySystem === 'string' && /widevine|playready/i.test(keySystem))
                answer.catch(() => { drmRejected = true; later(1500); });
              return answer;
            };
          }
          setTimeout(check, late ? 1500 : 3000);
          setTimeout(check, 8000);
        })();
        """;

    // O mesmo script corrido no fim do carregamento, para o 1.º documento de cada aba (nasce sem os scripts precoces:
    // docs/motor.md, F2). Não houve wrappers a tempo de ouvir as perguntas da página; valem o estado (erros, <source>
    // falhados) e, só como sinal fraco, um vídeo parado. Se o script precoce correu, não faz nada.
    public const string LateWatchScript = "(() => { const __eterisLate = true;\n" + WatchScript + "\n})();";

    // Motivo do aviso: format (o formato falhou), patented-only (só pediu H.264/AAC e ficou parado), drm (conteúdo
    // protegido), frame-player (leitor conhecido de outro site num iframe). EngineSaidNo: o motor deste documento
    // respondeu «não» ao H.264/AAC.
    public sealed record VideoReport(string Reason, bool Strong, bool FromFrame, bool EngineSaidNo = false);

    private static readonly string[] Reasons = ["format", "patented-only", "drm", "frame-player"];

    public static bool IsReport(string? message) => Parse(message) is not null;

    // «razão [força] [origem] [no]». O formato antigo (só a razão) conta como forte, no topo.
    public static VideoReport? Parse(string? message)
    {
        if (message is null || !message.StartsWith(ReportPrefix, StringComparison.Ordinal)) return null;
        var parts = message[ReportPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4 || !Reasons.Contains(parts[0])) return null;
        var strength = parts.Length > 1 ? parts[1] : "strong";
        var origin = parts.Length > 2 ? parts[2] : "top";
        if (strength is not ("strong" or "weak") || origin is not ("top" or "frame")) return null;
        if (parts.Length > 3 && parts[3] != "no") return null;
        return new VideoReport(parts[0], strength == "strong", origin == "frame", parts.Length > 3);
    }

    public enum Decision { None, Offer, Switch }

    // O que a janela faz com um aviso: trocar sozinha só com um sinal forte no próprio documento de topo e nunca com DRM
    // (o CDM fala com servidores de terceiros: só depois de o utilizador escolher). Um site que o utilizador mandou
    // voltar ao motor principal nunca troca sozinho — fica só o botão.
    // mainHasCodecs (Tabs/PlatformCodecs.cs): o motor principal toca H.264/AAC — um erro de vídeo, um leitor parado ou um
    // leitor noutro iframe já não querem dizer «formato patenteado», a não ser que o motor tenha respondido «não»
    // (EngineSaidNo); nesse caso vale tudo como no motor sem codecs. O DRM continua a ser só uma pergunta.
    public static Decision Decide(VideoReport report, bool optedOut, string? site = null, bool mainHasCodecs = false)
    {
        if (report.Reason is "drm") return Decision.Offer;
        if (mainHasCodecs && !report.EngineSaidNo) return Decision.None;
        if (report.Reason is "frame-player") return Decision.Offer;
        if (!report.Strong || report.FromFrame || optedOut) return Decision.Offer;
        if (site is not null && OfferOnlySites.Contains(site)) return Decision.Offer;
        return Decision.Switch;
    }

    // Sites onde o motor de vídeo é pior do que um vídeo parado, a não ser que o utilizador o peça: o Reddit pede
    // reCAPTCHA no perfil novo do WebView2 («Prove your humanity», diagnóstico de vídeo).
    public static readonly IReadOnlySet<string> OfferOnlySites = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "reddit.com" };

    // Sites sobretudo de vídeo e só em H.264 (medido: docs/motor.md, V2): abrem logo no motor de vídeo, sem passar pelo
    // motor principal, quando o motor principal não tem codecs (runtime do NuGet, ou a sonda de PlatformCodecs disse
    // «não»). Por domínio registável. Os mistos (X, Reddit, g1, BBC, YouTube) ficam de fora: perderiam a cosmética do
    // Escudo, e o Reddit pede reCAPTCHA no motor de vídeo.
    public static readonly IReadOnlySet<string> KnownH264Sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "twitch.tv", "kick.com", "vimeo.com", "dailymotion.com", "tiktok.com", "instagram.com",
    };

    // Abre no motor de vídeo: lembrado, ou na lista embutida se o motor principal não tem codecs — nunca um site que o
    // utilizador mandou voltar. Com codecs, «lembrado» é a lista própria desse modo (AppData.IsVideoSite): os sites
    // lembrados quando faltavam os codecs não contam.
    public static bool OpensInVideoEngine(string site, bool remembered, bool optedOut, bool mainHasCodecs = false)
        => !optedOut && (remembered || (!mainHasCodecs && KnownH264Sites.Contains(site)));

    // O site pode ter deixado dados no perfil do motor de vídeo: abriu lá (lembrado, ou da lista embutida) ou o
    // utilizador mandou-o voltar ao motor principal (o botão tira-o dos lembrados e põe-no nos excluídos; o que o
    // WebView2 guardou continua lá). Decide se o Triturar e companhia abrem o perfil do WebView2 sem aba viva dele.
    // O tempo máximo de uma limpeza no perfil do motor de vídeo (arrancar o runtime a frio, abrir o controlador
    // escondido e apagar): passado isto a limpeza responde «não foi possível» e nada fica pendurado.
    public static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(15);

    public static bool MayHoldData(string site, bool remembered, bool optedOut)
        => remembered || optedOut || KnownH264Sites.Contains(site);

    // As caixas de «Apagar dados» no WebView2: cookies e dados de sites → tudo o que é do site (cookies e todo o
    // armazenamento); cache → a cache do disco; histórico → o histórico próprio do WebView2 e o das transferências
    // (inteiros, seja qual for o período: não se veem em lado nenhum). 0: nada a apagar.
    public static CoreWebView2BrowsingDataKinds DataKinds(bool history, bool cookies, bool cache)
    {
        CoreWebView2BrowsingDataKinds kinds = 0;
        if (cookies) kinds |= CoreWebView2BrowsingDataKinds.AllSite;
        if (cache) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
        if (history) kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory;
        return kinds;
    }

    // Argumentos do motor do Edge: sem SmartScreen (enviaria os endereços visitados à Microsoft), sem a interface
    // do Office nos PDFs, sem tráfego de fundo, sem pings de auditoria de links e sem o «component updater» (no
    // diagnóstico descarregou componentes sem ação do utilizador). Com «bloquear reprodução automática», a mesma
    // política do motor principal. Fixos por pasta de dados: aplicam-se ao arrancar, como no motor principal.
    // A política de IP do WebRTC é a do motor principal (Shields/EngineHardening.cs); o nome do interruptor foi lido no
    // libcef 156; medido que o WebView2 158.0.4370.16 o aceita (sem candidatos ICE nem pedido ao STUN, docs/paridade.md §7).
    public const string BaseArguments =
        "--disable-features=msSmartScreenProtection,msWebOOUI,msPdfOOUI --disable-background-networking --no-pings --disable-component-update"
        + $" --{Shields.EngineHardening.WebRtcPolicySwitch}={Shields.EngineHardening.WebRtcPolicy}";

    public static string BrowserArguments(bool blockAutoplay)
        => blockAutoplay ? BaseArguments + " --autoplay-policy=document-user-activation-required" : BaseArguments;

    // A língua do WebView2 é a do motor principal (a 1.ª da lista Accept-Language com que ele arrancou); sem ela, pt-PT.
    public static string Language(string started)
        => started.Split(',')[0].Trim() is { Length: > 0 } first ? first : "pt-PT";

    // O que estas abas têm e não têm em relação ao motor principal (eteris://privacy/ e o Escudo dizem-no). Não é uma
    // promessa medida: «o que o próprio WebView2 pede à Microsoft» está por medir.
    public const string PrivacyNote =
        "As abas no motor de vídeo (WebView2, da Microsoft) têm as listas do Escudo, o pedido de privacidade (GPC), a prevenção de rastreio estrita do WebView2 e as notificações recusadas. "
        + "Não têm o DNS cifrado, o filtro de cookies de terceiros do Éteris, o referrer reduzido nem o bloqueio da rede local, e o que o próprio WebView2 pede à Microsoft ainda não foi medido.";

    public const string ShieldCookiesLine = "·  Cookies de terceiros: prevenção de rastreio do motor de vídeo (não o filtro do Éteris)";
    public const string ShieldWebRtcLine = "·  WebRTC: política pedida ao motor de vídeo";
    public const string ShieldDnsLine = "·  DNS do sistema: o DNS cifrado não chega ao motor de vídeo";

    // Só os endereços web passam para o motor de vídeo; as páginas internas (eteris://) ficam no motor principal.
    public static bool CanHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // Aberto diretamente, o motor principal transfere estes formatos em vez de os reproduzir (não os conhece sem os
    // codecs patenteados): HLS (.m3u8) e áudio AAC. MP4/MOV entram pelo tipo.
    private static readonly string[] PlayableExtensions = [".m3u8", ".m4a", ".aac"];

    private static readonly HashSet<string> PlayableMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl",
        "audio/mp4", "audio/x-m4a", "audio/aac", "audio/aacp", "video/mp4", "video/quicktime", "video/x-m4v",
    };

    public static bool IsPlayableLink(string? url)
        => CanHost(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && PlayableExtensions.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    // Uma transferência que é afinal vídeo/áudio para ver: tipo reproduzível (ou extensão .m3u8/.m4a/.aac), endereço
    // web e sem «attachment» (o site não pediu para guardar o ficheiro).
    public static bool IsPlayableDownload(string? url, string? mimeType, string? contentDisposition)
    {
        if (!CanHost(url)) return false;
        if (contentDisposition?.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) == true) return false;
        var mime = mimeType?.Split(';')[0].Trim();
        return (mime is { Length: > 0 } && PlayableMimeTypes.Contains(mime)) || IsPlayableLink(url);
    }

    // Onde está o maior vídeo/áudio que já começou (segundos; 0 se nenhum): a troca de motor retoma aí.
    public const string PositionScript = """
        (() => {
          let at = 0, area = -1;
          document.querySelectorAll('video,audio').forEach(m => {
            const b = m.getBoundingClientRect(), a = b.width * b.height;
            if (m.currentTime > 0 && a > area) { at = m.currentTime; area = a; }
          });
          return at;
        })()
        """;

    // Um ficheiro de vídeo/áudio aberto diretamente (não uma página): retoma-se com o fragmento #t= (Media Fragments).
    public static bool IsDirectMedia(string? url)
        => CanHost(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && DirectMediaExtensions.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] DirectMediaExtensions = [".mp4", ".m4v", ".mov", ".m4a", ".aac", ".mp3", ".webm", ".ogg", ".m3u8"];

    // Título de uma página do motor de vídeo. Um ficheiro de vídeo/áudio aberto diretamente não tem título (e o WebView2
    // nem avisa a mudança): como no Chrome, fica o nome do ficheiro, ou o anfitrião quando o caminho não tem nome.
    public static string PageTitle(string? documentTitle, string? url)
    {
        if (!string.IsNullOrWhiteSpace(documentTitle) && !string.Equals(documentTitle, url, StringComparison.OrdinalIgnoreCase)
            && !documentTitle.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return documentTitle;
        return UntitledTitle(url);
    }

    // Título de uma página do motor principal: igual ao que chega, exceto num ficheiro de vídeo/áudio aberto
    // diretamente, a que o Chromium dá o próprio endereço (sem esquema nem «www.») como título — aí, o mesmo critério
    // do motor de vídeo (PageTitle): o nome do ficheiro.
    public static string MainEngineTitle(string? title, string? url)
    {
        if (IsDirectMedia(url) && (string.IsNullOrWhiteSpace(title) || SameAsAddress(title, url!))) return UntitledTitle(url);
        return string.IsNullOrEmpty(title) ? url ?? string.Empty : title;
    }

    private static bool SameAsAddress(string title, string url)
    {
        static string Bare(string text)
        {
            var bare = text.Trim();
            foreach (var scheme in (string[])["https://", "http://"])
                if (bare.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) bare = bare[scheme.Length..];
            if (bare.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) bare = bare[4..];
            bare = bare.TrimEnd('/');
            try { return Uri.UnescapeDataString(bare); }
            catch (UriFormatException) { return bare; }
        }
        return string.Equals(Bare(title), Bare(url), StringComparison.OrdinalIgnoreCase);
    }

    public static string UntitledTitle(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url ?? string.Empty;
        var name = uri.AbsolutePath;
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.Length > 0)
        {
            try { return Uri.UnescapeDataString(name); }
            catch (UriFormatException) { return name; }
        }
        return uri.Host.Length > 0 ? uri.Host : url!;
    }

    public static string WithoutFragment(string url)
    {
        var hash = url.IndexOf('#');
        return hash < 0 ? url : url[..hash];
    }

    // A posição do vídeo da página do motor principal só vale para essa mesma página. Um ficheiro de média que chega
    // como transferência (o desvio do DownloadHandler) ou uma navegação para um site de vídeo trocam de motor com a
    // página ANTERIOR ainda à vista: o #t= dela ia parar ao ficheiro novo (medido: um .aac abria em #t=2).
    // Retomar o vídeo no motor de vídeo onde estava no motor principal: no primeiro «loadedmetadata» da página de topo
    // (a posição vem do documento de topo, PositionScript), uma só vez. O registo sai depois do primeiro documento
    // (MainWindow.VideoEngine.cs, ResumeAtAsync); a guarda do topo impede que os iframes desse documento saltem.
    public static string ResumeScript(double seconds)
    {
        var at = seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return "(() => { if (window !== top) return; const at = " + at + "; let done = false;"
            + " addEventListener('loadedmetadata', e => { const v = e.target;"
            + " if (done || !(v instanceof HTMLMediaElement) || !(v.duration > at)) return; done = true; v.currentTime = at; }, true); })();";
    }

    public static bool ResumesFrom(string? shownAddress, string target)
        => shownAddress is not null && string.Equals(WithoutFragment(shownAddress), WithoutFragment(target), StringComparison.Ordinal);

    // Ficheiro de vídeo/áudio que o motor principal desviou para aqui por não o conseguir tocar e que o WebView2 também
    // não reconhece (o tipo vem errado do servidor: um .aac como audio/vnd.dlna.adts, o tipo que o registo do Windows dá
    // ao .aac, por exemplo no http.server do Python). O WebView2 gravava-o em Transferências, com o painel dele, e a aba
    // ficava vazia. Só a navegação da própria aba (não um link para guardar dentro da página) e sem «attachment».
    public static bool PlaysInsteadOfDownload(string downloadUrl, string? mainNavigation, string? mimeType, string? contentDisposition)
        => mainNavigation is not null && string.Equals(WithoutFragment(downloadUrl), WithoutFragment(mainNavigation), StringComparison.Ordinal)
           && IsPlayableDownload(downloadUrl, mimeType, contentDisposition);

    // No lugar do ficheiro, no mesmo endereço, uma página com o leitor do próprio WebView2: um <audio>/<video> com o
    // ficheiro como fonte reconhece o formato pelo conteúdo (não pelo tipo que o servidor deu).
    public static string PlayerPage(string url, string? mimeType)
    {
        var bare = WithoutFragment(url);
        var audio = mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
                    || AudioExtensions.Any(e => bare.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        var name = Uri.TryCreate(bare, UriKind.Absolute, out var uri) && Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)) is { Length: > 0 } file
            ? file : bare;
        var tag = audio ? "audio" : "video";
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"color-scheme\" content=\"dark light\"><title>"
            + System.Net.WebUtility.HtmlEncode(name) + "</title><style>html,body{margin:0;height:100%;background:#0e0e0e;"
            + "display:flex;align-items:center;justify-content:center}video{max-width:100%;max-height:100%}"
            + "audio{width:min(640px,90vw)}</style></head><body><" + tag + " controls autoplay src=\""
            + System.Net.WebUtility.HtmlEncode(url) + "\"></" + tag + "></body></html>";
    }

    private static readonly string[] AudioExtensions = [".aac", ".m4a", ".mp3", ".ogg", ".oga", ".opus", ".wav", ".flac"];

    public static string WithTimeFragment(string url, double seconds)
    {
        var hash = url.IndexOf('#');
        var bare = hash < 0 ? url : url[..hash];
        return bare + "#t=" + Math.Floor(seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

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

    // Os pedidos que o bloqueador do motor de vídeo vê: todos menos os segmentos de vídeo/áudio (cada um passava pela
    // thread da janela, com uma ida ao motor do Escudo; as listas quase não têm regras $media).
    public static bool IsFiltered(CoreWebView2WebResourceContext context) => context != CoreWebView2WebResourceContext.Media;
}
