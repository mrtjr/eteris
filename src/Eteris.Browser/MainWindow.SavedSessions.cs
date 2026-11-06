using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Palette;

namespace Eteris.Browser;

// «Sessões guardadas»: guardar as abas desta janela com um nome e reabri-las mais tarde, tudo pela paleta.
public partial class MainWindow
{
    private IEnumerable<PaletteCommand> SessionCommands()
    {
        var saved = AppData.SavedSessions();
        var tabs = SessionTabs();
        if (!IsPrivate && tabs.Count > 0)
            yield return new("", "Guardar abas como sessão",
                $"{Plural(tabs.Count, "aba", "abas")} desta janela, para reabrir mais tarde pela paleta", null, () => SaveSession(tabs));

        foreach (var session in saved.Take(8))
        {
            var entry = session;
            var when = entry.CreatedUtc.ToLocalTime().ToString("d MMM, HH:mm", PtCulture);
            yield return new("", $"Abrir sessão «{entry.Name}»", $"{Plural(entry.Tabs.Count, "aba", "abas")} · guardada a {when}", null,
                () => OpenSession(entry));
        }
        foreach (var session in saved.Take(8))
        {
            var entry = session;
            yield return new("", $"Apagar sessão «{entry.Name}»", "As abas abertas não são afetadas", null, () =>
            {
                AppData.DeleteSession(entry.Id);
                ShowToast($"Sessão «{entry.Name}» apagada");
            });
        }
    }

    private List<SavedTab> SessionTabs()
        => Tabs.Tabs.Where(t => !NewTabPage.IsNewTab(t.Address)).Select(t => new SavedTab(t.Address, t.Title)).ToList();

    private void SaveSession(IReadOnlyList<SavedTab> tabs)
    {
        var name = SavedSessionNames.Suggest(tabs, AppData.SavedSessions().Select(s => s.Name).ToList());
        AppData.SaveSession(name, tabs);
        ShowToast($"Sessão «{name}» guardada — reabra-a pela paleta (Ctrl+K)");
    }

    // Uma janela só com «nova aba» é reaproveitada; senão a sessão abre numa janela nova.
    private void OpenSession(SavedSession session)
    {
        var addresses = session.Tabs.Select(t => t.Url).ToList();
        if (addresses.Count == 0) return;
        if (IsPrivate || Tabs.Tabs.Any(t => !NewTabPage.IsNewTab(t.Address)))
        {
            OpenWindowWith(addresses, session.Tabs.Select(t => t.Title).ToList(), 0);
            return;
        }
        LoadUrl(addresses[0]);
        foreach (var tab in session.Tabs.Skip(1)) Tabs.AppendSleeping(tab.Url, tab.Title);
    }

    private static readonly System.Globalization.CultureInfo PtCulture = new("pt-PT");

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
