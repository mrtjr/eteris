namespace Eteris.Browser.Browser;

// A que pedido do «Espreitar» pertence o fim de um carregamento da aba escondida. O pedido é o número de série
// corrente no instante em que o documento é confirmado (FrameLoadStart), não no instante em que acaba de carregar:
// com dois pedidos seguidos (A e depois B), o fim de A pode chegar depois de B ter sido pedido e, sem isto, o
// texto de A aparecia no cartão de B. Um pedido que ainda não confirmou documento é cancelado pelo seguinte, por
// isso o documento confirmado é sempre o do pedido corrente nesse instante.
public sealed class GlanceLoads
{
    private readonly Lock _gate = new();
    private int? _committed;

    // O frame principal confirmou um documento; currentSerial é o pedido corrente nesse instante.
    public void Committed(int currentSerial)
    {
        lock (_gate) _committed = currentSerial;
    }

    // O frame principal acabou de carregar: devolve o pedido a que esse documento pertence (null sem confirmação).
    public int? Finished()
    {
        lock (_gate) return _committed;
    }
}
