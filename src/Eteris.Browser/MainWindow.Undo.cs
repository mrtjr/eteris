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
    private UndoEntry RememberUndo(string label, Action undo) => _undo.Push(label, undo);

    // Aviso com «Anular» (e, opcionalmente, outra ação antes dele).
    private void ShowUndoToast(string message, string label, Action undo)
    {
        var entry = _undo.Push(label, undo);
        ShowToast(message, new ToastAction("Anular", () =>
        {
            if (_undo.Undo(entry)) ShowToast($"Anulado — {entry.Label}");
        }));
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
        RememberUndo($"Fechar {ToastPolicy.Quote(tab.DisplayTitle)}", () => Tabs.ReopenClosed(entry));
    }

    // Fechos em bloco (selecionadas, duplicadas): um aviso com «Anular» que as reabre todas.
    private void OfferReopen(IReadOnlyList<ClosedTab> before, string message, string label)
    {
        var entries = Tabs.ClosedSince(before);
        if (entries.Count == 0)
        {
            ShowToast(message);
            return;
        }
        ShowUndoToast(message, label, () => Tabs.ReopenAll(entries));
    }

    // ---------- Ficheiros guardados ----------

    private static ToastAction OpenFileAction(string path)
        => new("Abrir", () => { if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });

    private static ToastAction ShowInFolderAction(string path)
        => new("Mostrar na pasta", () => { if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\""); });

    // ---------- Abas que saem da faixa (Feito, Adiar, Arquivo) ----------

    private void PutBackDone(TabsViewModel.TabSnapshot snapshot)
    {
        AppData.UndoDone(snapshot.Address);
        Tabs.PutBack(snapshot);
        ScheduleSessionSave();
    }

    private void PutBackSnoozed(TabsViewModel.TabSnapshot snapshot)
    {
        if (AppData.SnoozedTabs().Where(s => s.Url == snapshot.Address).MaxBy(s => s.Id) is { } snoozed) AppData.ForgetSnoozed(snoozed.Id);
        Tabs.PutBack(snapshot);
        ScheduleSessionSave();
    }

    private void PutBackArchived(IReadOnlyList<TabsViewModel.TabSnapshot> snapshots)
    {
        var archived = AppData.ArchivedTabs();
        // Pela ordem dos lugares originais: cada aba volta ao seu sítio.
        foreach (var snapshot in snapshots.OrderBy(s => s.Index))
        {
            if (archived.Where(a => a.Url == snapshot.Address).MaxBy(a => a.Id) is { } entry) AppData.ForgetArchived(entry.Id);
            Tabs.PutBack(snapshot, activate: false);
        }
        ScheduleSessionSave();
    }
}
