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

    // Ctrl+Shift+E: abre a barra lateral nas notas (ou fecha-a, se já é o que está à vista).
    private void ToggleNotes() => ToggleSidebar(SidebarPanels.Notes);

    private void LoadNotes()
    {
        _notesLoading = true;
        NotesText.Text = Notes.Load();
        _notesLoading = false;
        NotesText.CaretIndex = NotesText.Text.Length;
        NotesStatus.Text = "Guardado só neste computador";
    }

    private void NotesText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_notesLoading) return;
        NotesStatus.Text = "A escrever…";
        _notesSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => FlushNotes(), Dispatcher);
        _notesSave.Stop();
        _notesSave.Start();
    }

    private void FlushNotes()
    {
        // Só há o que gravar com o painel à vista e texto alterado desde a última gravação (o temporizador a contar).
        var pending = _notesSave is { IsEnabled: true };
        _notesSave?.Stop();
        if (!SidebarShows(SidebarPanels.Notes) || !pending) return;
        try
        {
            Notes.Save(NotesText.Text);
            NotesStatus.Text = $"Guardado às {DateTime.Now:HH:mm} · só neste computador";
        }
        catch (Exception) { NotesStatus.Text = "Não foi possível guardar as notas"; }
    }

    private void NotesInsertPage_Click(object sender, RoutedEventArgs e)
    {
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
