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
