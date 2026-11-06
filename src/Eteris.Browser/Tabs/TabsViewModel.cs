using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Eteris.Browser.Tabs;

public partial class TabsViewModel : ObservableObject
{
    private readonly Stack<(int Index, string Address)> _closed = new();

    public ObservableCollection<BrowserTab> Tabs { get; } = new();

    [ObservableProperty]
    private BrowserTab? _active;

    public BrowserTab NewTab(string address, bool activate = true)
    {
        var tab = new BrowserTab(address);
        var index = activate || Active is null ? Tabs.Count : Tabs.IndexOf(Active) + 1;
        Tabs.Insert(index, tab);
        if (activate || Active is null) Active = tab;
        return tab;
    }

    // Devolve true quando a última aba foi fechada (a janela deve fechar, como no Chrome).
    public bool Close(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return Tabs.Count == 0;

        _closed.Push((index, tab.Address));
        var wasActive = ReferenceEquals(tab, Active);
        Tabs.RemoveAt(index);

        if (Tabs.Count == 0)
        {
            Active = null;
            return true;
        }

        if (wasActive) Active = Tabs[Math.Min(index, Tabs.Count - 1)];
        return false;
    }

    public BrowserTab? ReopenClosed()
    {
        if (_closed.Count == 0) return null;
        var (index, address) = _closed.Pop();
        var tab = new BrowserTab(address);
        Tabs.Insert(Math.Min(index, Tabs.Count), tab);
        Active = tab;
        return tab;
    }

    public void SelectNext() => SelectOffset(1);

    public void SelectPrevious() => SelectOffset(-1);

    public void SelectByNumber(int number)
    {
        if (Tabs.Count == 0 || number < 1) return;
        Active = number >= 9 ? Tabs[^1] : Tabs[Math.Min(number, Tabs.Count) - 1];
    }

    private void SelectOffset(int offset)
    {
        if (Tabs.Count == 0 || Active is null) return;
        var index = (Tabs.IndexOf(Active) + offset + Tabs.Count) % Tabs.Count;
        Active = Tabs[index];
    }
}
