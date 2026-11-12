using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class NotesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-notas-" + Guid.NewGuid() + ".md");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
    }

    // NAV-01, DAD-08: duas janelas com as Notas abertas — cada uma grava por cima do texto que leu.
    private static readonly DateTime When = new(2026, 11, 11, 9, 30, 0);

    [Fact]
    public void Save_WhenDiskChangedSinceLoad_KeepsBothTexts()
    {
        var store = new NotesStore(_path);
        store.Save("a");
        var loaded = store.Load();
        new NotesStore(_path).Save("a\nlista");
        var (result, text) = store.SaveOver(loaded, "ab", When);
        Assert.Equal(NotesSave.Merged, result);
        Assert.Equal("a\nlista\n\n— Escrito noutra janela (09:30) —\nab\n", text);
        Assert.Equal(text, store.Load());
    }

    [Fact]
    public void Save_WhenDiskIsTheBase_ReplacesIt()
    {
        var store = new NotesStore(_path);
        store.Save("a");
        Assert.Equal((NotesSave.Saved, "a b"), store.SaveOver("a", "a b", When));
        Assert.Equal("a b", store.Load());
    }

    [Fact]
    public void Save_WhenFileUnreadable_DoesNotOverwrite()
    {
        var store = new NotesStore(_path);
        store.Save("guardado");
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(NotesSave.Unreadable, store.SaveOver("guardado", "novo", When).Result);
        Assert.Equal(NotesSave.Unreadable, store.SaveOver(null, "novo", When).Result);
        Assert.Equal("guardado", store.Load());
    }

    [Theory]
    [InlineData("a", "a\nlista", "a", "a\nlista")]                                               // esta janela não mudou nada
    [InlineData("a\nb", "a", "a\nb\nc", "a\n\n— Escrito noutra janela (09:30) —\nc\n")]          // o «b» apagado na outra não volta
    [InlineData("", "", "novo", "novo\n")]
    public void Merge_AddsOnlyTheLinesThisWindowWrote(string baseText, string disk, string mine, string expected)
        => Assert.Equal(expected, NotesStore.Merge(baseText, disk, mine, When));

    [Fact]
    public void Window_SavesOverItsBase_AndListensToTheOtherWindows()
    {
        var notes = Source("MainWindow.Notes.cs");
        Assert.Contains("Notes.SaveOver(_notesBase, NotesText.Text, DateTime.Now)", notes);
        Assert.DoesNotContain("Notes.Save(NotesText.Text)", notes);
        Assert.Contains("NotesStore.Saved += OnSaved;", notes);
        Assert.Contains("NotesStore.Saved -= OnSaved;", notes);
        Assert.Contains("NotesText.IsReadOnly = text is null;", notes);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));

    [Fact]
    public void SaveAndLoad_RoundTripsAccentsAndLines()
    {
        var store = new NotesStore(_path);
        Assert.Equal(string.Empty, store.Load());
        store.Save("Ideias:\n- pastéis de nata\n");
        Assert.Equal("Ideias:\n- pastéis de nata\n", new NotesStore(_path).Load());
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void PageLine_HasTitleAndCleanAddress()
    {
        Assert.Equal("- Fado – Wikipédia — https://pt.wikipedia.org/wiki/Fado",
            NotesStore.PageLine("Fado –  Wikipédia", "https://pt.wikipedia.org/wiki/Fado?utm_source=x"));
        Assert.Equal("- https://a.pt/", NotesStore.PageLine("", "https://a.pt/"));
    }

    [Theory]
    [InlineData("", 0, "- L\n", 4)]
    [InlineData("abc", 3, "abc\n- L\n", 8)]
    [InlineData("abc\n", 4, "abc\n- L\n", 8)]
    [InlineData("ab\ncd", 2, "ab\n- L\ncd", 7)]
    public void Insert_PutsTheLineOnItsOwn(string text, int caret, string expected, int expectedCaret)
    {
        var (result, newCaret) = NotesStore.Insert(text, caret, "- L");
        Assert.Equal(expected, result);
        Assert.Equal(expectedCaret, newCaret);
    }
}
