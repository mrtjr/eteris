namespace Eteris.Browser.Infrastructure;

// A caixa onde chegam os lançamentos de outros processos (um link, --private) entre o canal começar a escutar (logo a
// seguir à instância única, em Program.Main) e a janela existir (App.OnStartup): as mensagens ficam guardadas, por ordem,
// e entregam-se quando o tratador se liga; depois entregam-se diretamente (INF-05).
public sealed class InstanceInbox
{
    private readonly Lock _gate = new();
    private readonly List<string[]> _held = [];
    private Action<string[]>? _handler;

    public void Post(string[] args)
    {
        Action<string[]>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
            {
                _held.Add(args);
                return;
            }
        }
        handler(args);
    }

    public void Attach(Action<string[]> handler)
    {
        List<string[]> held;
        lock (_gate)
        {
            _handler = handler;
            held = [.. _held];
            _held.Clear();
        }
        foreach (var args in held) handler(args);
    }
}
