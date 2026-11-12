using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class ClosedWindowsTests
{
    private static ClosedWindow Window(params string[] urls) => new(urls, urls, 0);

    [Fact]
    public void PopReturnsMostRecent()
    {
        var closed = new ClosedWindows();
        closed.Push(Window("https://a.pt/"));
        closed.Push(Window("https://b.pt/", "https://c.pt/"));
        Assert.Equal(2, closed.Latest!.Addresses.Count);
        Assert.Equal("https://b.pt/", closed.Pop()!.Addresses[0]);
        Assert.Equal("https://a.pt/", closed.Pop()!.Addresses[0]);
        Assert.Null(closed.Pop());
    }

    [Fact]
    public void EmptyWindows_AreIgnoredAndListIsCapped()
    {
        var closed = new ClosedWindows();
        closed.Push(Window());
        Assert.Equal(0, closed.Count);
        for (var i = 0; i < 15; i++) closed.Push(Window($"https://s{i}.pt/"));
        Assert.Equal(ClosedWindows.Max, closed.Count);
    }

    [Fact]
    public void Clear_LeavesNothingToReopen()
    {
        var closed = new ClosedWindows();
        closed.Push(Window("https://a.pt/"));
        closed.Push(Window("https://b.pt/"));
        closed.Clear();
        Assert.Null(closed.Latest);
        Assert.Equal(0, closed.Count);
    }

    [Fact]
    public void RemoveWhere_DropsWindowsWithTheSite()
    {
        var closed = new ClosedWindows();
        closed.Push(Window("https://a.pt/"));
        closed.Push(Window("https://www.exemplo.pt/x", "https://b.pt/"));
        // Uma aba do site guardada noutro Espaço da janela fechada também conta.
        closed.Push(new ClosedWindow(["https://c.pt/"], ["C"], 0,
            [new Data.SessionSpace("Um", 0), new Data.SessionSpace("Dois", 1, Tabs: new Data.Session(["https://loja.exemplo.pt/"], 0))], SpaceIndex: 0));
        Assert.Equal(2, closed.RemoveWhere(w => w.AllAddresses.Any(a => Data.SiteScope.Belongs(a, "exemplo.pt"))));
        Assert.Equal("https://a.pt/", closed.Pop()!.Addresses[0]);
        Assert.Null(closed.Pop());
    }
}
