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
}
