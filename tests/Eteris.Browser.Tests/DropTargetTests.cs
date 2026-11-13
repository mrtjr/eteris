using System.IO;
using Eteris.Browser.Omnibox;

namespace Eteris.Browser.Tests;

public class DropTargetTests
{
    [Theory]
    [InlineData("https://www.publico.pt/x", "https://www.publico.pt/x")]
    [InlineData("  github.com/mrtjr  ", "https://github.com/mrtjr")]
    [InlineData("receita de bacalhau", "https://duckduckgo.com/?q=receita%20de%20bacalhau")]
    public void DroppedText_BecomesAddressOrSearch(string text, string expected)
        => Assert.Equal(expected, DropTarget.FromText(text, SearchEngine.DuckDuckGo));

    [Fact]
    public void DroppedText_UsesOnlyFirstLine()
        => Assert.Equal("https://a.pt/", DropTarget.FromText("https://a.pt/\nhttps://b.pt/", SearchEngine.DuckDuckGo));

    [Theory]
    [InlineData(@"C:\Users\x\Documents\nota.pdf", "file:///C:/Users/x/Documents/nota.pdf")]
    [InlineData(@"C:\pasta com espaço\página.html", "file:///C:/pasta%20com%20espa%C3%A7o/p%C3%A1gina.html")]
    public void DroppedFile_BecomesFileUrl(string path, string expected)
        => Assert.Equal(expected, DropTarget.FromFile(path));

    [Fact]
    public void EmptyText_IsNull()
        => Assert.Null(DropTarget.FromText("   ", SearchEngine.DuckDuckGo));

    [Theory]
    [InlineData("file://srv/a")]
    [InlineData("eteris://settings/")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/x.html")]
    public void DroppedText_NonWebScheme_IsNotOpened(string text)
        => Assert.Null(DropTarget.FromText(text, SearchEngine.DuckDuckGo));

    [Fact]
    public void DropUrls_FileDrop_IsIgnoredWhenTheDragWasTaintedByTheRenderer()
    {
        var mouse = File.ReadAllText(Path.Combine(Eteris.Browser.Tests.Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.Mouse.cs"));
        Assert.Contains("!data.GetDataPresent(DropTarget.RendererTaintFormat)", mouse);
    }
}
