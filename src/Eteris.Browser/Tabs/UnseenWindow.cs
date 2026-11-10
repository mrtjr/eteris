using System.Collections.Concurrent;

namespace Eteris.Browser.Tabs;

// E8 do plano de desempenho da 0.2.14 (ajuste e8, ligado por omissão desde o diagnóstico D4): com a janela minimizada,
// noutra área de trabalho ou com a sessão bloqueada, a página à vista também se esconde, como no Chrome, no Firefox e
// no Brave (o rastreio de oclusão do Chromium só segue janelas de topo; a página do Éteris vive numa janela filha).
// Regra pura; a janela em MainWindow.Unseen.cs.
public static class UnseenWindow
{
    public enum Reason { None, Minimized, OtherDesktop, Locked }

    // Porque é que a janela não se vê (a primeira que se aplica). A janela tapada por outra aplicação fica de fora: pede o
    // cálculo de oclusão com histerese (custo M no plano). O D4 mediu-a: tapada, a página continua a desenhar, com ou
    // sem este ajuste (o Chrome, o Firefox e o Brave param). Lacuna em aberto, por fazer.
    public static Reason Why(bool minimized, bool cloaked, bool locked)
        => minimized ? Reason.Minimized : locked ? Reason.Locked : cloaked ? Reason.OtherDesktop : Reason.None;

    // O relógio que vê a área de trabalho (o DWM não avisa esta janela quando a tapa ou destapa). Anda enquanto a janela
    // está à vista e não ativa (mudar de área de trabalho desativa-a) — também logo depois de restaurada sem ser
    // ativada. E, escondida por estar noutra área, anda até se voltar a ver, mesmo ativa: ativar uma janela de outra área
    // (o ícone do Éteris, o Alt+Tab) é o que leva o Windows a mudar de área, e a ativação chega antes de o DWM a destapar.
    // Minimizada não precisa dele: quem a traz de volta é a mudança de estado.
    public static bool Watches(bool active, bool minimized, Reason reason)
        => reason == Reason.OtherDesktop || (!active && !minimized);

    // De quanto em quanto tempo: devagar à espera de a janela ir para outra área, depressa à espera de ela voltar (é o
    // tempo que a página fica por desenhar numa janela que já se vê).
    public static TimeSpan WatchEvery(Reason reason)
        => reason == Reason.OtherDesktop ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);

    // Uma página com som, com multimédia já começada (o Picture-in-Picture de um vídeo), com a câmara, o microfone ou o
    // ecrã partilhados, ou com uma janela Picture-in-Picture de documento, continua à vista: escondê-la cortava a chamada,
    // pausava o vídeo flutuante ou mudava o que o site faz com a janela tapada.
    public static bool MayHide(bool audible, MediaState media, bool capturing, bool pictureInPicture)
        => !audible && media == MediaState.None && !capturing && !pictureInPicture;

    // Script que diz se a página tem uma janela Picture-in-Picture aberta (vídeo ou documento): «1» ou «».
    public const string PictureInPictureScript =
        "(function(){try{if(document.pictureInPictureElement)return '1';"
        + "if(window.documentPictureInPicture&&documentPictureInPicture.window)return '1';}catch(e){}return '';})()";
}

// Páginas com captura (câmara, microfone, ecrã) autorizada desde a última navegação do documento principal. O motor da
// CEF 156 não avisa quando a captura acaba (não há OnMediaAccessChange no CefSharp): conta até o documento mudar.
public static class CaptureSessions
{
    private static readonly ConcurrentDictionary<int, byte> Active = new();

    public static void Started(int browserId) => Active[browserId] = 0;

    public static void Ended(int browserId) => Active.TryRemove(browserId, out _);

    public static bool IsCapturing(int browserId) => Active.ContainsKey(browserId);
}
