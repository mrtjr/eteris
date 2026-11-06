using System.Windows;
using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// «Reabrir janela fechada»: as abas de uma janela normal fechada ficam guardadas enquanto o Éteris estiver aberto.
public partial class MainWindow
{
    private static readonly ClosedWindows s_closedWindows = new();

    private void RememberWhenClosing()
    {
        Closing += (_, _) =>
        {
            // A última janela é guardada pela sessão; as privadas não deixam rasto.
            var others = Application.Current.Windows.OfType<MainWindow>().Count(w => !ReferenceEquals(w, this));
            if (IsPrivate || others == 0) return;
            var tabs = Tabs.Tabs.Where(t => !NewTabPage.IsNewTab(t.Address)).ToList();
            var active = Tabs.Active is { } a ? Math.Max(0, tabs.IndexOf(a)) : 0;
            s_closedWindows.Push(new ClosedWindow(tabs.Select(t => t.Address).ToList(), tabs.Select(t => t.Title).ToList(), active));
        };
    }

    private static void ReopenClosedWindow()
    {
        if (s_closedWindows.Pop() is not { } closed) return;
        OpenWindowWith(closed.Addresses, closed.ActiveIndex);
    }

    private static void OpenWindowWith(IReadOnlyList<string> addresses, int activeIndex)
    {
        if (addresses.Count == 0) return;
        var window = new MainWindow(addresses[0], isPrivate: false);
        window.Show();
        foreach (var address in addresses.Skip(1)) window.Tabs.NewTab(address, activate: false);
        if (activeIndex < window.Tabs.Tabs.Count) window.Tabs.Active = window.Tabs.Tabs[activeIndex];
    }
}
