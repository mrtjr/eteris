using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Anular: as ações destrutivas do Éteris ficam numa pilha (por janela, só em memória). O aviso mostra «Anular»;
// Ctrl+Z anula a última quando o foco não está num campo de texto nem na página (aí o Ctrl+Z é da página); a paleta
// também a oferece.
public partial class MainWindow
{
    private readonly UndoStack _undo = new();

    public sealed record ToastAction(string Label, Action Run);

    // Guarda sem aviso (fechar uma aba: avisar a cada Ctrl+W seria ruído — o Ctrl+Z chega).
    private UndoEntry RememberUndo(string label, Func<bool> undo, IEnumerable<string>? addresses = null)
        => _undo.Push(label, undo, addresses);

    // Aviso com «Anular» (e, opcionalmente, outra ação antes dele). O botão e o Ctrl+Z tiram a mesma entrada da pilha.
    private void ShowUndoToast(string message, string label, Func<bool> undo, IEnumerable<string>? addresses = null)
    {
        var entry = _undo.Push(label, undo, addresses);
        ShowToast(message, new ToastAction("Anular", () =>
        {
            if (_undo.Undo(entry)) ShowToast($"Anulado — {entry.Label}");
        }));
    }

    private void ShowUndoToast(string message, string label, Action undo)
        => ShowUndoToast(message, label, () =>
        {
            undo();
            return true;
        });

    // «Triturar» e «Esquecer» um site: nem as fechadas recentes nem a pilha de anulação o reabrem — nem mostram o
    // título dele na paleta — em nenhuma janela do mesmo contexto.
    private void ForgetSiteInUndo(string site)
    {
        foreach (var window in IsPrivate ? [this] : NormalWindows())
        {
            window.Tabs.ForgetClosed(c => Data.SiteScope.Belongs(c.Address, site));
            window._undo.RemoveWhere(e => e.Addresses.Any(a => Data.SiteScope.Belongs(a, site)));
        }
        // As janelas fechadas (só as normais as guardam) também deixam de poder reabrir o site.
        if (!IsPrivate) s_closedWindows.RemoveWhere(w => w.AllAddresses.Any(a => Data.SiteScope.Belongs(a, site)));
    }

    private void UndoLast()
    {
        if (_undo.UndoLast() is { } entry) ShowToast($"Anulado — {entry.Label}");
        else ShowToast("Nada para anular");
    }

    // O Ctrl+Z é do Éteris só quando ninguém mais o quer: nem um campo de texto (omnibox, nomes, notas) nem a página.
    private static bool TextInputHasFocus()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };

    // ---------- Fechar abas ----------

    // Depois de fechar uma aba (Ctrl+W, ×, clique do meio): o Ctrl+Z reabre-a no mesmo lugar.
    private void RememberClosedTab(BrowserTab tab, IReadOnlyList<ClosedTab> before)
    {
        if (Tabs.ClosedSince(before) is not [var entry]) return;
        RememberUndo($"Fechar {ToastPolicy.Quote(tab.DisplayTitle)}", () => Tabs.ReopenClosed(entry) is not null, [entry.Address]);
    }

    // Fechos em bloco (selecionadas, duplicadas, as outras, as à direita, o grupo): um aviso com «Anular» que as
    // reabre todas — e o Ctrl+Z anula o bloco, não o fecho anterior.
    private void OfferReopen(IReadOnlyList<ClosedTab> before, string message, string label)
    {
        var entries = Tabs.ClosedSince(before);
        if (entries.Count == 0)
        {
            ShowToast(message);
            return;
        }
        ShowUndoToast(message, label, () => Tabs.ReopenAll(entries) > 0, entries.Select(e => e.Address));
    }

    private void CloseInBulk(Action close)
    {
        var before = Tabs.RecentlyClosed;
        close();
        var count = Tabs.ClosedSince(before).Count;
        if (count > 0) OfferReopen(before, $"{TabSelection.Count(count)} {(count == 1 ? "fechada" : "fechadas")}", $"Fechar {TabSelection.Count(count)}");
    }

    // ---------- Ficheiros guardados ----------

    private static ToastAction OpenFileAction(string path)
        => new("Abrir", () => { if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });

    private static ToastAction ShowInFolderAction(string path)
        => new("Mostrar na pasta", () => { if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\""); });

    // ---------- Abas que saem da faixa (Feito, Adiar, Arquivo) ----------

    // Só repõem o que ainda lá está: uma aba já reposta pelo Radar (ou que acordou sozinha) não volta em duplicado.
    private bool PutBackDone(TabsViewModel.TabSnapshot snapshot)
    {
        if (!AppData.DoneSince(DateTime.UtcNow.AddDays(-1)).Any(d => d.Url == snapshot.Address)) return false;
        AppData.UndoDone(snapshot.Address);
        Tabs.PutBack(snapshot);
        ScheduleSessionSave();
        return true;
    }

    private bool PutBackSnoozed(TabsViewModel.TabSnapshot snapshot)
    {
        if (AppData.SnoozedTabs().Where(s => s.Url == snapshot.Address).MaxBy(s => s.Id) is not { } snoozed) return false;
        AppData.ForgetSnoozed(snoozed.Id);
        Tabs.PutBack(snapshot);
        ScheduleSessionSave();
        return true;
    }

    private bool PutBackArchived(IReadOnlyList<TabsViewModel.TabSnapshot> snapshots)
    {
        var archived = AppData.ArchivedTabs();
        var restored = 0;
        // Pela ordem dos lugares originais: cada aba volta ao seu sítio.
        foreach (var snapshot in snapshots.OrderBy(s => s.Index))
        {
            if (archived.Where(a => a.Url == snapshot.Address).MaxBy(a => a.Id) is not { } entry) continue;
            AppData.ForgetArchived(entry.Id);
            Tabs.PutBack(snapshot, activate: false);
            restored++;
        }
        ScheduleSessionSave();
        return restored > 0;
    }
}
