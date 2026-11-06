using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Eteris.Browser.Tabs;

public partial class TabsViewModel : ObservableObject
{
    public const int MaxClosed = 25;

    // Abas fechadas, a mais recente no fim.
    private readonly List<ClosedTab> _closed = [];

    public IReadOnlyList<ClosedTab> RecentlyClosed => _closed.AsEnumerable().Reverse().ToList();

    public ObservableCollection<BrowserTab> Tabs { get; } = new();

    [ObservableProperty]
    private BrowserTab? _active;

    public BrowserTab NewTab(string address, bool activate = true)
    {
        var tab = new BrowserTab(address);
        var index = activate || Active is null ? Tabs.Count : Math.Max(Tabs.IndexOf(Active) + 1, PinnedCount);
        Tabs.Insert(index, tab);
        if (activate || Active is null) Active = tab;
        return tab;
    }

    // Abas da sessão anterior, pela ordem original; só a ativa carrega, as outras ficam a dormir até serem abertas.
    public void Restore(IReadOnlyList<string> addresses, int activeIndex, IReadOnlyList<string>? titles = null,
        IReadOnlyList<bool>? pinned = null)
    {
        for (var i = 0; i < addresses.Count; i++)
        {
            var tab = new BrowserTab(addresses[i])
            {
                IsSleeping = i != activeIndex,
                IsPinned = pinned is not null && i < pinned.Count && pinned[i],
            };
            if (titles is not null && i < titles.Count && titles[i].Length > 0) tab.Title = titles[i];
            Tabs.Add(tab);
        }
        if (Tabs.Count > 0) Active = Tabs[Math.Clamp(activeIndex, 0, Tabs.Count - 1)];
    }

    // Devolve true quando a última aba foi fechada (a janela deve fechar, como no Chrome).
    public bool Close(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return Tabs.Count == 0;

        if (!Browser.NewTabPage.IsNewTab(tab.Address))
        {
            _closed.Add(new ClosedTab(index, tab.Address, tab.Title));
            if (_closed.Count > MaxClosed) _closed.RemoveAt(0);
        }
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

    public BrowserTab? ReopenClosed() => _closed.Count == 0 ? null : ReopenClosed(_closed[^1]);

    public BrowserTab? ReopenClosed(ClosedTab entry)
    {
        if (!_closed.Remove(entry)) return null;
        var tab = new BrowserTab(entry.Address) { Title = entry.Title };
        Tabs.Insert(Math.Min(entry.Index, Tabs.Count), tab);
        Active = tab;
        return tab;
    }

    public void ForgetClosed() => _closed.Clear();

    public int PinnedCount => Tabs.Count(t => t.IsPinned);

    // Abas fixadas e normais não se misturam: cada uma só se move dentro do seu grupo.
    public void Move(BrowserTab tab, int newIndex)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0) return;
        var pinned = PinnedCount;
        var to = tab.IsPinned ? Math.Clamp(newIndex, 0, pinned - 1) : Math.Clamp(newIndex, pinned, Tabs.Count - 1);
        if (to != from) Tabs.Move(from, to);
    }

    // Fixar leva a aba para o fim do grupo fixado; desafixar, para logo a seguir a ele.
    public void TogglePin(BrowserTab tab)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0) return;
        var pin = !tab.IsPinned;
        tab.IsPinned = false;
        var target = PinnedCount;
        if (from != target) Tabs.Move(from, target);
        tab.IsPinned = pin;
    }

    public void CloseOthers(BrowserTab keep)
    {
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep) && !t.IsPinned).ToList()) Close(tab);
        Active = keep;
    }

    public void CloseToRight(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        foreach (var other in Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList()) Close(other);
        if (Active is null || !Tabs.Contains(Active)) Active = tab;
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

public sealed record ClosedTab(int Index, string Address, string Title);
