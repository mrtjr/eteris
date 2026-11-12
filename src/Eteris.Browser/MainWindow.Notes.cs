using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Sidebar;

namespace Eteris.Browser;

// Painel «Notas» da barra lateral: um bloco de notas ao lado da página, que se guarda sozinho (só neste computador).
public partial class MainWindow
{
    private static NotesStore Notes => new(Path.Combine(AppPaths.Root, "notas.md"));

    private DispatcherTimer? _notesSave;
    private bool _notesLoading;
    // O texto tal como veio do disco ou foi gravado por esta janela (nunca o da caixa): é por cima dele que se grava,
    // e o que outra janela gravou entretanto junta-se em vez de se perder. null = as notas não se leram.
    private string? _notesBase;
    // A janela que está a gravar agora: o aviso NotesStore.Saved que ela própria dispara não a recarrega.
    private static MainWindow? s_notesWriter;

    // Ctrl+Shift+E: abre a barra lateral nas notas (ou fecha-a, se já é o que está à vista).
    private void ToggleNotes() => ToggleSidebar(SidebarPanels.Notes);

    private static string NotesWhere => Sync.SyncService.Enabled ? "Guardado e sincronizado (cifrado)" : "Guardado só neste computador";

    // Notas que não se leram (ficheiro preso por outro programa) ficam só de leitura e vazias, com aviso, em vez de
    // parecerem vazias e serem gravadas por cima. keepCaret: recarregada porque outra janela gravou.
    private void LoadNotes(bool keepCaret = false)
    {
        var text = Notes.TryLoad();
        _notesBase = text;
        NotesText.IsReadOnly = text is null;
        ShowNotesText(text ?? string.Empty, keepCaret);
        NotesStatus.Text = text is null ? "Não foi possível ler as notas — feche e abra o painel para tentar de novo" : NotesWhere;
    }

    // Põe o texto na caixa sem contar como escrita; o cursor fica onde estava (limitado) ou no fim.
    private void ShowNotesText(string text, bool keepCaret)
    {
        var caret = NotesText.CaretIndex;
        _notesLoading = true;
        NotesText.Text = text;
        _notesLoading = false;
        NotesText.CaretIndex = keepCaret ? Math.Min(caret, NotesText.Text.Length) : NotesText.Text.Length;
    }

    private void NotesText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_notesLoading) return;
        NotesStatus.Text = "A escrever…";
        _notesSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => FlushNotes(), Dispatcher);
        // Depois de uma gravação falhada o intervalo passou a 5 s: escrever volta aos 600 ms.
        _notesSave.Interval = TimeSpan.FromMilliseconds(600);
        _notesSave.Stop();
        _notesSave.Start();
    }

    private void FlushNotes(bool closing = false)
    {
        // Só há o que gravar com o painel à vista e texto alterado desde a última gravação (o temporizador a contar).
        var pending = _notesSave is { IsEnabled: true };
        _notesSave?.Stop();
        if (!SidebarShows(SidebarPanels.Notes) || !pending) return;
        try
        {
            (NotesSave Result, string Text) saved;
            s_notesWriter = this;
            try { saved = Notes.SaveOver(_notesBase, NotesText.Text, DateTime.Now); }
            finally { s_notesWriter = null; }
            if (saved.Result == NotesSave.Saved)
            {
                _notesBase = saved.Text;
                NotesStatus.Text = $"Guardado às {DateTime.Now:HH:mm} · {(Sync.SyncService.Enabled ? "sincronizado, cifrado" : "só neste computador")}";
                return;
            }
            if (saved.Result == NotesSave.Merged)
            {
                _notesBase = saved.Text;
                ShowNotesText(saved.Text, keepCaret: true);
                NotesStatus.Text = $"Guardado às {DateTime.Now:HH:mm} — juntou-se o que foi escrito noutra janela";
                return;
            }
        }
        catch (Exception) { }
        NotesStatus.Text = "Não foi possível guardar as notas";
        // Volta a tentar daqui a 5 s; o fecho da janela nunca deixa o temporizador ligado.
        if (closing || _notesSave is null) return;
        _notesSave.Interval = TimeSpan.FromSeconds(5);
        _notesSave.Start();
    }

    // Outra janela gravou as notas: esta, com o painel à vista e nada por gravar, mostra o texto novo.
    private void WatchNotes()
    {
        if (IsPrivate) return;
        void OnSaved()
        {
            if (ReferenceEquals(s_notesWriter, this)) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (SidebarShows(SidebarPanels.Notes) && _notesSave is not { IsEnabled: true }) LoadNotes(keepCaret: true);
            });
        }
        NotesStore.Saved += OnSaved;
        Closed += (_, _) => NotesStore.Saved -= OnSaved;
    }

    private void NotesInsertPage_Click(object sender, RoutedEventArgs e)
    {
        if (NotesText.IsReadOnly) return;
        if (Tabs.Active is not { } tab || !Browser.ReaderScripts.IsCandidate(tab.Address)) return;
        var (text, caret) = NotesStore.Insert(NotesText.Text, NotesText.CaretIndex, NotesStore.PageLine(tab.Title, tab.Address));
        NotesText.Text = text;
        NotesText.CaretIndex = caret;
        NotesText.Focus();
    }

    private void NotesText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseSidebar();
    }
}
