using Eteris.Browser.Sidebar;

namespace Eteris.Browser;

// Depois de importar um perfil cifrado (Definições › Sincronização): os Espaços em falta entram na primeira janela
// (sem mudar o que está à vista), as notas abertas recarregam e aparece o resumo.
public partial class MainWindow
{
    internal static void AfterProfileImport(IReadOnlyList<(string Name, int Color, string? Icon)> spaces, string summary)
    {
        if (NormalWindows().FirstOrDefault(w => !w._forgetOnCloseDone) is not { } window) return;
        foreach (var (name, color, icon) in spaces)
            if (!window.Tabs.Workspaces.Any(w => string.Equals(w.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                window.Tabs.NewWorkspace(name, color, icon, activate: false);
        foreach (var open in NormalWindows())
            if (open.SidebarShows(SidebarPanels.Notes)) open.LoadNotes();
        window.ShowToast(summary);
    }
}
