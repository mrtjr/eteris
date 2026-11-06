using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// Painel «Notas»: um bloco de notas por cima da página, que se guarda sozinho (só neste computador).
public partial class MainWindow
{
    private static NotesStore Notes => new(Path.Combine(AppPaths.Root, "notas.md"));

    private DispatcherTimer? _notesSave;
    private bool _notesLoading;

    private void ToggleNotes()
    {
        if (NotesPanel.Visibility == Visibility.Visible)
        {
            CloseNotes();
            return;
        }
        _notesLoading = true;
        NotesText.Text = Notes.Load();
        _notesLoading = false;
        NotesText.CaretIndex = NotesText.Text.Length;
        NotesStatus.Text = "Guardado só neste computador";
        NotesPanel.Visibility = Visibility.Visible;
        NotesText.Focus();
    }

    private void CloseNotes()
    {
        FlushNotes();
        NotesPanel.Visibility = Visibility.Collapsed;
        ActiveBrowser?.Focus();
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
        _notesSave?.Stop();
        if (NotesPanel.Visibility != Visibility.Visible) return;
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

    private void NotesClose_Click(object sender, RoutedEventArgs e) => CloseNotes();

    private void NotesText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseNotes();
    }
}
