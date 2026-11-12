namespace Eteris.Browser.Tabs;

// Spaces: os Espaços da janela (como na sessão), com as abas dos que não estavam à vista; null sem Espaços.
// Metas: nome próprio, emoji, cor, nota e tranca das abas da faixa, pela mesma ordem.
public sealed record ClosedWindow(IReadOnlyList<string> Addresses, IReadOnlyList<string> Titles, int ActiveIndex,
    IReadOnlyList<Data.SessionSpace>? Spaces = null, int SpaceIndex = 0, IReadOnlyList<Data.SessionTabMeta>? Metas = null)
{
    // Há alguma coisa a reabrir: abas na faixa ou nalgum outro Espaço.
    public bool HasTabs => Addresses.Count > 0 || (Spaces?.Any(s => s.Tabs is { Urls.Count: > 0 }) ?? false);

    // Todas as abas que voltam ao reabrir: as da faixa e as guardadas nos outros Espaços (o Espaço à vista não leva
    // cópia, as abas dele são as da faixa).
    public int TabCount => Addresses.Count + (Spaces?.Where((s, i) => i != SpaceIndex).Sum(s => s.Tabs?.Urls.Count ?? 0) ?? 0);

    // Espaços com abas (o da faixa conta sempre que tem abas): para dizer «em 3 Espaços» na paleta.
    public int SpacesWithTabs => Spaces is null ? 1
        : Spaces.Where((s, i) => i == SpaceIndex ? Addresses.Count > 0 : s.Tabs is { Urls.Count: > 0 }).Count();

    // Todos os endereços guardados: os da faixa e os das abas dos outros Espaços (para «Triturar» um site os encontrar).
    public IEnumerable<string> AllAddresses
        => Addresses.Concat(Spaces?.SelectMany(s => s.Tabs?.Urls ?? []) ?? []);

    public string ReopenLabel => TabCount == 1 ? "Reabrir janela fechada (1 aba)" : $"Reabrir janela fechada ({TabCount} abas)";
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

    // «Esquecer»: nenhuma janela fechada fica para reabrir (não guardam a hora, saem todas).
    public void Clear()
    {
        lock (_lock) _windows.Clear();
    }

    // «Triturar» um site: tira as janelas fechadas que o tenham. Devolve quantas saíram.
    public int RemoveWhere(Func<ClosedWindow, bool> match)
    {
        lock (_lock) return _windows.RemoveAll(w => match(w));
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
