namespace Eteris.Browser.Infrastructure;

// Trabalho do arranque que a 1.ª página não precisa (lista de atalhos, procura de atualizações…): corre só depois de a
// 1.ª página aparecer. Até lá o fio da interface tem de ficar livre: a janela do CEF nasce filha de uma janela do WPF e
// o Windows entrega as mensagens dessa criação no fio da interface — cada milissegundo de trabalho ali, antes de ele
// voltar a esvaziar a fila, atrasa a página outro tanto (medido: a lista de atalhos, 84 ms, atrasava a 1.ª pintura
// 69 ms). Sem 1.ª página (a 1.ª aba é do motor de vídeo, ou a página não apresenta), corre ao fim de Fallback.
public sealed class FirstPaint(Action<Action> schedule)
{
    public static readonly TimeSpan Fallback = TimeSpan.FromSeconds(4);

    private readonly List<Action> _queue = [];
    private readonly Lock _gate = new();
    private bool _done;

    // O do Éteris: cada tarefa corre na thread da interface, com prioridade de fundo (depois da disposição e do desenho).
    public static FirstPaint App { get; } = new(action => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
        action, System.Windows.Threading.DispatcherPriority.Background));

    public bool HasHappened { get { lock (_gate) return _done; } }

    // Corre «action» depois da 1.ª página (já, se ela já apareceu).
    public void Then(Action action)
    {
        lock (_gate)
        {
            if (!_done)
            {
                _queue.Add(action);
                return;
            }
        }
        schedule(action);
    }

    // A 1.ª página apareceu (ou desistiu-se de esperar): o que estava à espera corre, pela ordem em que foi pedido.
    public void Signal()
    {
        List<Action> pending;
        lock (_gate)
        {
            if (_done) return;
            _done = true;
            pending = [.. _queue];
            _queue.Clear();
        }
        foreach (var action in pending) schedule(action);
    }

    // Rede de segurança: sem sinal até «after», dá-se por aparecida.
    public void SignalAfter(TimeSpan after) => _ = Task.Delay(after).ContinueWith(_ => Signal(), TaskScheduler.Default);
}
