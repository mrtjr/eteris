namespace Eteris.Browser.Tabs;

// Spaces: os Espaços da janela (como na sessão), com as abas dos que não estavam à vista; null sem Espaços.
public sealed record ClosedWindow(IReadOnlyList<string> Addresses, IReadOnlyList<string> Titles, int ActiveIndex,
    IReadOnlyList<Data.SessionSpace>? Spaces = null, int SpaceIndex = 0)
{
    // Há alguma coisa a reabrir: abas na faixa ou nalgum outro Espaço.
    public bool HasTabs => Addresses.Count > 0 || (Spaces?.Any(s => s.Tabs is { Urls.Count: > 0 }) ?? false);
}

// Janelas normais fechadas nesta sessão (a mais recente no topo). As privadas nunca entram aqui.
public sealed class ClosedWindows
{
    public const int Max = 10;

    private readonly List<ClosedWindow> _windows = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get { lock (_lock) return _windows.Count; }
    }

    public ClosedWindow? Latest
    {
        get { lock (_lock) return _windows.Count > 0 ? _windows[^1] : null; }
    }

    public void Push(ClosedWindow window)
    {
        if (!window.HasTabs) return;
        lock (_lock)
        {
            _windows.Add(window);
            if (_windows.Count > Max) _windows.RemoveAt(0);
        }
    }

    public ClosedWindow? Pop()
    {
        lock (_lock)
        {
            if (_windows.Count == 0) return null;
            var last = _windows[^1];
            _windows.RemoveAt(_windows.Count - 1);
            return last;
        }
    }
}
