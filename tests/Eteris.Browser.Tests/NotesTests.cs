using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class NotesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-notas-" + Guid.NewGuid() + ".md");

    public void Dispose() => File.Delete(_path);

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
