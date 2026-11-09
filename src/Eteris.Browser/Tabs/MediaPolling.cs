namespace Eteris.Browser.Tabs;

// Quem a central de multimédia sonda em cada ronda (E4 do plano de desempenho da 0.2.14, ajuste e4). Cada sondagem é um
// Runtime.evaluate que acorda o renderer por fora dos temporizadores da página e fura a limitação que o Blink faz às
// abas escondidas (uma vez por minuto depois de 5 min). Ronda de 2 s só para as abas que se veem (a ativa, as metades do
// ecrã dividido, as quentes) e as que já têm multimédia ou som (para apanhar a pausa e o fim); as outras de 30 em 30 s,
// e logo a seguir a um carregamento ou a um título novo (um vídeo que começou a tocar sozinho muda muitas vezes o título).
public static class MediaPolling
{
    public static readonly TimeSpan Round = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Background = TimeSpan.FromSeconds(30);

    // last: a última sondagem desta aba (null: nunca), no relógio monotónico de quem chama; nudged: houve um carregamento
    // ou um título novo desde então.
    public static bool Due(bool seen, MediaState media, bool audible, TimeSpan? last, bool nudged, TimeSpan now)
        => seen || media != MediaState.None || audible || nudged || last is not { } at || now - at >= Background;

    // Estado de sondagem de uma aba (o empurrão chega das threads do motor).
    public sealed class Poll
    {
        private volatile bool _nudged;

        public TimeSpan? Last { get; private set; }

        public bool Nudged => _nudged;

        public void Nudge() => _nudged = true;

        public void Polled(TimeSpan now)
        {
            Last = now;
            _nudged = false;
        }
    }
}
