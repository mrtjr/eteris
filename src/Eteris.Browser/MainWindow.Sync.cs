using System.Text.Json.Nodes;
using Eteris.Browser.Sidebar;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// A sincronização e as janelas: importar um perfil cifrado, e o que a sincronização lê e aplica nos Espaços e nas notas
// (sempre na thread da interface, chamado por Sync/BrowserProfile).
public partial class MainWindow
{
    // O separador carregou outro documento: o kit de recuperação aberto nele fecha-se (thread do CEF: só um lock e uma
    // comparação).
    private static void KitTabNavigated(int browserId) => Sync.SyncService.OpenKit.TabNavigated(browserId);

    // Depois de importar um perfil cifrado (Definições › Sincronização): os Espaços em falta entram na primeira janela
    // (sem mudar o que está à vista), as notas abertas recarregam e aparece o resumo.
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

    // A sessão ficou iniciada no navegador do sistema: chama a atenção para o Éteris (qualquer thread). Pisca a janela
    // normal mais à mão e tenta trazê-la para a frente (o Windows costuma recusar: fica o piscar). Não a restaura nem
    // maximiza; sem janela normal, nada.
    internal static void SignInArrived()
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (NormalWindows().Where(w => !w._forgetOnCloseDone).OrderByDescending(w => w.IsActive).FirstOrDefault() is not { } window) return;
            if (!Infrastructure.WindowAttention.ShouldAsk(window.IsActive)) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Infrastructure.WindowAttention.Flash(hwnd);
            Infrastructure.WindowAttention.TryForeground(hwnd);
        });

    // Os Espaços da primeira janela normal, pela ordem (null sem janela: a sincronização lê a sessão gravada).
    internal static IReadOnlyList<(string Name, int Color, string? Icon)>? SyncSpaces()
        => NormalWindows().FirstOrDefault(w => !w._forgetOnCloseDone) is { } window
            ? window.Tabs.Workspaces.Select(w => (w.Name, w.ColorIndex, w.Icon)).ToList()
            : null;

    // Um Espaço vindo de outro aparelho: entra (vazio, sem mudar o que está à vista) ou muda de cor, emoji e lugar.
    // Apagado noutro aparelho, só sai daqui se estiver vazio e fora de vista — as abas de cada Espaço são deste
    // computador e não se fecham por uma decisão tomada noutro. Devolve false quando não aplicou.
    internal static bool ApplySyncedSpace(string name, JsonObject? data)
    {
        if (NormalWindows().FirstOrDefault(w => !w._forgetOnCloseDone) is not { } window) return false;
        var tabs = window.Tabs;
        var existing = tabs.Workspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (data is null)
        {
            if (existing is null) return true;
            if (existing.IsCurrent || existing.TabCount > 0 || existing.StoredTabs.Count > 0 || tabs.Workspaces.Count < 2) return false;
            return tabs.RemoveWorkspace(existing);
        }
        if (Workspace.NormalizeName(name) != name) return false;
        var color = data["color"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 0;
        var icon = data["icon"] is JsonValue i && i.TryGetValue<string>(out var ic) ? Workspace.NormalizeIcon(ic) : null;
        existing ??= tabs.NewWorkspace(name, color, icon, activate: false);
        existing.ColorIndex = Workspace.NormalizeColor(color);
        existing.Icon = icon;
        if (data["order"] is JsonValue o && o.TryGetValue<int>(out var order) && order >= 0 && order < tabs.Workspaces.Count)
        {
            var at = tabs.Workspaces.IndexOf(existing);
            if (at != order) tabs.Workspaces.Move(at, order);
        }
        return true;
    }

    // As notas vindas de outro aparelho. Não se aplicam enquanto o utilizador está a escrever nelas (a gravação ainda
    // não saiu): ficam para a ronda seguinte, depois de a alteração dele seguir.
    internal static bool ApplySyncedNotes(string text)
    {
        if (NormalWindows().Any(w => w._notesSave is { IsEnabled: true })) return false;
        Notes.Save(text);
        foreach (var open in NormalWindows())
            if (open.SidebarShows(SidebarPanels.Notes)) open.LoadNotes();
        return true;
    }
}
