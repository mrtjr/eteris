using System.IO;

namespace Eteris.Browser.Tabs;

// Motor com codecs da plataforma (docs/motor.md, «Motor com codecs»; build/cef-codecs): a compilação própria da CEF
// toca H.264/HEVC pela placa gráfica (D3D11VideoDecoder) e AAC pelo Media Foundation, sem nenhum descodificador com
// patentes dentro do libcef. Quando vem na instalação, o motor de vídeo (WebView2) deixa de ser a regra para os sites
// só em H.264 e passa a ser o recurso. Mas «ter o motor» não chega: sem descodificação por hardware (máquina virtual,
// placa na lista negra, controlador básico) ou num Windows N sem o Media Feature Pack, o próprio motor diz «não» ao
// H.264 ou ao AAC — e um MP4 H.264 tocava só o som. Por isso uma sonda, uma vez por sessão, pergunta ao motor.
public static class PlatformCodecs
{
    // Missing: o motor principal não toca H.264+AAC (runtime do NuGet, ou a sonda disse «não») — tudo como antes: a
    // lista de sites só em H.264 abre no motor de vídeo e os sinais do WatchScript contam.
    // Unprobed: a compilação própria está instalada e a sonda ainda não respondeu — conta como Present (o motor do
    // WatchScript diz por si, em cada aviso, se respondeu «não»: VideoEngine.VideoReport.EngineSaidNo).
    // Present: a sonda confirmou H.264 e AAC no motor principal.
    public enum Support { Missing, Unprobed, Present }

    // Marca da compilação própria na instalação: o csproj copia o manifest.json do runtime para licenses\cef-codecs
    // (com os avisos de licença dela). O libcef.dll não serve de marca: tem a mesma versão que o do NuGet.
    public static readonly string MarkerPath = Path.Combine("licenses", "cef-codecs", "manifest.json");

    public static Support Initial(bool ownRuntime) => ownRuntime ? Support.Unprobed : Support.Missing;

    // H.264 High 4.0 (o perfil comum a 1080p) e AAC-LC, pelo MediaSource — a mesma pergunta que os leitores fazem. Corre
    // só numa página interna (eteris:), onde não há o WatchScript a observar as perguntas nem nenhum site a ver.
    public const string ProbeScript = """
        (() => {
          const M = window.MediaSource;
          if (!M || typeof M.isTypeSupported !== 'function') return 'sem-mse';
          const h264 = M.isTypeSupported('video/mp4; codecs="avc1.640028"');
          const aac = M.isTypeSupported('audio/mp4; codecs="mp4a.40.2"');
          return (h264 ? 'h264' : '-') + ' ' + (aac ? 'aac' : '-');
        })()
        """;

    // O que a sonda devolveu: só «h264 aac» é Present; qualquer outra resposta é Missing; sem resposta (a página fechou,
    // tempo esgotado) fica por saber e volta a perguntar-se na próxima página interna.
    public static Support FromProbe(bool success, object? result)
        => !success || result is not string answer ? Support.Unprobed
            : answer == "h264 aac" ? Support.Present : Support.Missing;

    public static bool HasCodecs(Support support) => support != Support.Missing;

    public static bool IsProbePage(string? url) => url?.StartsWith("eteris:", StringComparison.OrdinalIgnoreCase) == true;

    // ---------- Estado da sessão (um por processo: todas as janelas partilham o motor) ----------

    private static int s_state = -1;
    private static int s_probing;

    public static Support Current
    {
        get
        {
            var state = Volatile.Read(ref s_state);
            if (state >= 0) return (Support)state;
            var initial = Initial(File.Exists(Path.Combine(AppContext.BaseDirectory, MarkerPath)));
            Interlocked.CompareExchange(ref s_state, (int)initial, -1);
            return (Support)Volatile.Read(ref s_state);
        }
    }

    public static bool MainHasCodecs => HasCodecs(Current);

    // Começa a sonda se ainda ninguém a começou (uma de cada vez); quem recebe true corre o ProbeScript e chama Record.
    public static bool TryStartProbe(string? url)
        => Current == Support.Unprobed && IsProbePage(url) && Interlocked.CompareExchange(ref s_probing, 1, 0) == 0;

    public static Support Record(bool success, object? result)
    {
        var answer = FromProbe(success, result);
        if (answer != Support.Unprobed) Interlocked.CompareExchange(ref s_state, (int)answer, (int)Support.Unprobed);
        Volatile.Write(ref s_probing, 0);
        return Current;
    }
}
