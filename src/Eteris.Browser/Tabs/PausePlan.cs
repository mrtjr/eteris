namespace Eteris.Browser.Tabs;

// Um motor vivo da janela quando a Pausa começa (ou quando nasce durante ela): a página de uma aba (de qualquer Espaço com
// motor), o motor de vídeo de uma aba ou um site fixado na barra lateral. «Muted»: calado pelo utilizador (a aba
// silenciada); um site fixado nunca está. «Media»: o que a sondagem da central de multimédia viu (MainWindow.Audio.cs).
public sealed record PauseEngine<T>(T Engine, bool Muted, MediaState Media);

// O que fazer ao voltar da Pausa: devolver o som a «Unmute» e oferecer «Retomar o que tocava (N)» para «Replay».
public sealed record PauseReturn<T>(IReadOnlyList<T> Unmute, IReadOnlyList<T> Replay)
{
    // Sem nada que tocava, não há aviso «Retomar».
    public bool OffersReplay => Replay.Count > 0;
}

// A Pausa (Ctrl+., MainWindow.Pause.cs), sem WPF: a partir dos motores vivos de uma janela, o que se cala e o que tocava.
//   - PlayingAudible: os que tocavam com som (Media == Playing e não silenciados) — são os N do «Retomar o que tocava (N)»;
//   - MutedByPause: todos os que ainda não estavam silenciados — os únicos a que a Pausa devolve o som.
// Ao voltar, um motor que entretanto fechou (aba fechada, adormecida, trocada de motor) já não está entre os de agora e
// fica de fora; um que o utilizador silenciou à mão durante a Pausa continua calado. Só no fio da interface.
public sealed class PausePlan<T> where T : class
{
    private readonly List<T> _engines = [];
    private readonly HashSet<T> _mutedBefore = new(ReferenceEqualityComparer.Instance);
    private readonly List<T> _playing = [];
    private readonly List<T> _mutedByPause = [];

    // Todos os motores do plano, por onde corre o script de marcar (que só mexe no que está a tocar).
    public IReadOnlyList<T> Engines => _engines;

    public IReadOnlyList<T> PlayingAudible => _playing;

    public IReadOnlyList<T> MutedByPause => _mutedByPause;

    public static PausePlan<T> From(IEnumerable<PauseEngine<T>> engines)
    {
        var plan = new PausePlan<T>();
        foreach (var engine in engines)
        {
            if (plan.Has(engine.Engine)) continue;
            plan.Add(engine);
            if (!engine.Muted && engine.Media == MediaState.Playing) plan._playing.Add(engine.Engine);
        }
        return plan;
    }

    // Um motor que nasce durante a Pausa (uma aba que um site abriu, um site fixado que volta a carregar): entra calado.
    public void Add(PauseEngine<T> engine)
    {
        if (Has(engine.Engine)) return;
        _engines.Add(engine.Engine);
        if (engine.Muted) _mutedBefore.Add(engine.Engine);
        else _mutedByPause.Add(engine.Engine);
    }

    // O script de marcar pausou «paused» elementos deste motor. A sondagem da multimédia de uma aba de fundo pode ter até
    // 30 s (MediaPolling.Background) e um site fixado nem é sondado: o que se pausou conta como «a tocar», se tinha som.
    public void Marked(T engine, int paused)
    {
        if (paused <= 0 || !Has(engine) || _mutedBefore.Contains(engine)) return;
        if (!_playing.Contains(engine, ReferenceEqualityComparer.Instance)) _playing.Add(engine);
    }

    // Uma aba fechada (ou o motor largado) sai do plano.
    public void Forget(T engine)
    {
        _engines.RemoveAll(e => ReferenceEquals(e, engine));
        _mutedBefore.Remove(engine);
        _playing.RemoveAll(e => ReferenceEquals(e, engine));
        _mutedByPause.RemoveAll(e => ReferenceEquals(e, engine));
    }

    // «now»: os motores vivos ao voltar, com o silêncio que o utilizador lhes deu (não o da Pausa).
    public PauseReturn<T> Return(IEnumerable<PauseEngine<T>> now)
    {
        var alive = new Dictionary<T, bool>(ReferenceEqualityComparer.Instance);
        foreach (var engine in now) alive[engine.Engine] = engine.Muted;
        var unmute = _mutedByPause.Where(e => alive.TryGetValue(e, out var muted) && !muted).ToList();
        var replay = _playing.Where(alive.ContainsKey).ToList();
        return new PauseReturn<T>(unmute, replay);
    }

    private bool Has(T engine) => _engines.Contains(engine, ReferenceEqualityComparer.Instance);
}
