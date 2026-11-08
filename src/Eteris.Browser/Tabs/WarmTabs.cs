namespace Eteris.Browser.Tabs;

// Abas «quentes»: as 1–2 páginas deixadas há menos tempo ficam, durante um período de graça curto, como estavam à vista
// — no tamanho inteiro, não minimizadas, só tapadas pela página da aba ativa — em vez de serem minimizadas logo. Voltar a
// uma delas é só subir a sua janela: o DWM já tem o conteúdo dela, sem nova disposição nem rasterização (o equivalente
// ao «saved frame» do Chrome, que guarda o último fotograma das 5–10 abas escondidas mais recentes até 5 min; estudo
// do Chromium, §1.2 e rec. 2). Uma aba fria (minimizada) leva o tempo da nova rasterização (T1/T5 em docs/motor.md).
//
// Custo: uma aba quente continua «visible» para o Chromium e desenha como se estivesse à vista — numa página com
// animação, ~9–11% de 1 núcleo (docs/paridade.md #1b); numa página parada, quase nada. Por isso é só durante a graça
// (no máximo Limit abas × Grace), nunca para abas com vídeo a tocar (um vídeo continuaria a ser descodificado e
// desenhado sem ninguém o ver) e nunca fora das condições de MainWindow.CanStayWarm (ecrã dividido, janela minimizada,
// aba com som, aba a passar para o motor de vídeo). Passada a graça, a aba é minimizada como antes: o custo de uma aba
// escondida volta a ser o de #1b.
public static class WarmTabs
{
    // Quanto tempo uma página deixada fica quente: chega para ir e voltar (Ctrl+Tab, comparar duas abas) sem que uma aba
    // animada esquecida gaste mais do que alguns segundos.
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(8);

    // Quantas páginas quentes, no máximo, ao mesmo tempo (as deixadas há menos tempo).
    public const int Limit = 2;

    // Quanto se espera pela verificação de vídeo a tocar; sem resposta a tempo conta como «a tocar» (a aba arrefece).
    public static readonly TimeSpan VideoCheckLimit = TimeSpan.FromMilliseconds(250);

    // «LeftAt» e «now» são leituras de um relógio que só anda para a frente (MainWindow.WarmClock, não a hora do sistema:
    // um acerto do relógio, NTP ou manual, arrefecia as quentes ou adiava a revisão).
    public readonly record struct Candidate<T>(T Page, TimeSpan LeftAt, bool Eligible, bool PlayingVideo);

    // As que ficam quentes agora: elegíveis, sem vídeo a tocar, dentro da graça e, destas, as Limit deixadas há menos
    // tempo. As outras arrefecem (são minimizadas).
    public static IReadOnlyList<T> Keep<T>(IEnumerable<Candidate<T>> candidates, TimeSpan now)
        => candidates
            .Where(c => c.Eligible && !c.PlayingVideo && Within(c, now))
            .OrderByDescending(c => c.LeftAt)
            .Take(Limit)
            .Select(c => c.Page)
            .ToList();

    // Quando voltar a decidir: quando a primeira das que ficam sai da graça. null: nenhuma fica.
    public static TimeSpan? NextReview<T>(IEnumerable<Candidate<T>> kept, TimeSpan now)
    {
        TimeSpan? next = null;
        foreach (var candidate in kept)
        {
            var left = candidate.LeftAt + Grace - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            if (next is null || left < next) next = left;
        }
        return next;
    }

    // Tamanho mínimo (CSS px) de um iframe de outro site para contar como leitor no PlayingVideoScript.
    public const int PlayerWidth = 200, PlayerHeight = 150;

    // Só um «não» claro deixa a aba quente: erro, tempo esgotado ou resposta estranha contam como vídeo a tocar.
    public static bool PlaysVideo(bool success, object? result) => !success || result is not false;

    // Algum <video> a tocar na página: no documento, nas raízes de sombra abertas (leitores como o do Reddit) e nos
    // iframes do mesmo site. Os iframes de outro site não se deixam ler, e o som deles também não se vê
    // (BrowserTab.IsAudible só lê o documento principal): um iframe de outro site com ar de leitor (pelo menos
    // PlayerWidth×PlayerHeight, com autoplay ou ecrã inteiro permitidos: YouTube, Vimeo, Spotify ou SoundCloud
    // embutidos) conta como «a tocar», por cautela; a página arrefece mesmo que o leitor esteja parado.
    public const string PlayingVideoScript = """
        (function () {
          try {
            var playing = function (root) {
              return Array.prototype.some.call(root.querySelectorAll('video'), function (v) { return !v.paused && !v.ended; });
            };
            var inShadow = function (doc) {
              var all = doc.querySelectorAll('*');
              for (var i = 0; i < all.length; i++) {
                if (all[i].shadowRoot && playing(all[i].shadowRoot)) return true;
              }
              return false;
            };
            if (playing(document) || inShadow(document)) return true;
            for (var f = 0; f < window.frames.length; f++) {
              try { if (playing(window.frames[f].document)) return true; } catch (e) { }
            }
            var frames = document.querySelectorAll('iframe');
            for (var k = 0; k < frames.length; k++) {
              var frame = frames[k], readable = false;
              try { readable = !!frame.contentDocument; } catch (e) { }
              if (readable) continue;
              var box = frame.getBoundingClientRect();
              if (box.width < 200 || box.height < 150) continue;
              var allow = (frame.getAttribute('allow') || '').toLowerCase();
              if (frame.hasAttribute('allowfullscreen') || allow.indexOf('autoplay') >= 0 || allow.indexOf('fullscreen') >= 0) return true;
            }
            return false;
          } catch (e) { return true; }
        })()
        """;

    private static bool Within<T>(Candidate<T> candidate, TimeSpan now)
    {
        var age = now - candidate.LeftAt;
        return age >= TimeSpan.Zero && age < Grace;
    }
}
