using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Palette;

namespace Eteris.Browser;

// «Sessões guardadas»: guardar as abas desta janela com um nome e reabri-las mais tarde, tudo pela paleta.
public partial class MainWindow
{
    private IEnumerable<PaletteCommand> SessionCommands()
    {
        // Numa janela privada não se mostra nada do perfil normal (como na nova aba privada).
        if (IsPrivate) yield break;
        var saved = AppData.SavedSessions();
        var tabs = SessionTabs();
        if (tabs.Count > 0)
            yield return new("", "Guardar abas como sessão",
                $"{Plural(tabs.Count, "aba", "abas")} desta janela{(Tabs.HasWorkspaces ? " (todos os Espaços)" : "")}, para reabrir mais tarde pela paleta",
                null, () => SaveSession(tabs));


        foreach (var session in saved.Take(8))
        {
            var entry = session;
            var when = entry.CreatedUtc.ToLocalTime().ToString("d MMM, HH:mm", PtCulture);
            yield return new("", $"Abrir sessão «{entry.Name}»", $"{Plural(entry.Tabs.Count, "aba", "abas")} · guardada a {when}", null,
                () => OpenSession(entry));
        }
        if (saved.Count > 0)
            yield return new("", "Gerir sessões guardadas", "Abrir, mudar o nome e apagar sessões", null,
                () => OpenInternal(BookmarksPage.Url + "#sessoes"));
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

    // Aberta a partir de eteris://bookmarks#sessoes.
    internal static void OpenSavedSessionWindow(SavedSession session)
        => OpenWindowWith(session.Tabs, 0);

    // A partir da nova aba: a janela dessa aba abre a sessão (e é reaproveitada se só tiver abas novas).
    internal static void OpenSavedSessionFrom(int browserId, SavedSession session)
    {
        if (WindowOf(browserId) is { } owner) owner.OpenSession(session);
        else OpenSavedSessionWindow(session);
    }

    internal static MainWindow? WindowOf(int browserId)
        => System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w._browsers.Values.Any(b =>
        {
            try { return b.IsBrowserInitialized && b.GetBrowser()?.Identifier == browserId; }
            catch (Exception) { return false; }
        }));

    // Exportar favoritos para Transferências (pela paleta ou pelo botão em eteris://bookmarks).
    internal void ExportBookmarks()
    {
        var marks = AppData.Bookmarks(b => b.All(100_000));
        if (marks.Count == 0)
        {
            ShowToast("Ainda não há favoritos para exportar");
            return;
        }
        try
        {
            var folder = Eteris.Browser.Downloads.DownloadPaths.DownloadsFolder();
            var path = Eteris.Browser.Downloads.DownloadPaths.Unique(folder, BookmarkExport.FileName(DateTime.Now));
            System.IO.File.WriteAllText(path, BookmarkExport.ToHtml(marks, DateTime.UtcNow), new System.Text.UTF8Encoding(false));
            ShowToast($"{(marks.Count == 1 ? "1 favorito exportado" : $"{marks.Count} favoritos exportados")} para Transferências › {System.IO.Path.GetFileName(path)}",
                ShowInFolderAction(path));
        }
        catch (Exception)
        {
            ShowToast("Não foi possível exportar os favoritos");
        }
    }

    // Todas as abas da janela: as da faixa e as dos outros Espaços (a reserva), pela ordem do seletor de Espaços.
    private List<SavedTab> SessionTabs()
        => Tabs.Workspaces.SelectMany(w => Tabs.TabsOf(w)).Where(t => !NewTabPage.IsNewTab(t.Address))
            .Select(t => new SavedTab(t.Address, t.Title, t.CustomTitle, t.Note)).ToList();

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
            OpenWindowWith(session.Tabs, 0);
            return;
        }
        LoadUrl(addresses[0]);
        Tabs.Active?.SetIdentity(session.Tabs[0].Name, null, null, session.Tabs[0].Note);
        foreach (var tab in session.Tabs.Skip(1)) Tabs.AppendSleeping(tab.Url, tab.Title).SetIdentity(tab.Name, null, null, tab.Note);
    }

    private static readonly System.Globalization.CultureInfo PtCulture = new("pt-PT");

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
