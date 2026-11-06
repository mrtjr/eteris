using System.IO;
using System.Windows;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public sealed class WindowPlacementTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-window-" + Guid.NewGuid() + ".json");
    private static readonly Rect Screen = new(0, 0, 2560, 1080);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        WindowPlacement.Save(_path, new WindowPlacement(100, 80, 1400, 900, Maximized: true));
        Assert.Equal(new WindowPlacement(100, 80, 1400, 900, true), WindowPlacement.Load(_path));
    }

    [Fact]
    public void MissingOrCorrupt_IsNull()
    {
        Assert.Null(WindowPlacement.Load(_path));
        File.WriteAllText(_path, "nope");
        Assert.Null(WindowPlacement.Load(_path));
    }

    [Fact]
    public void VisiblePlacement_IsKept()
        => Assert.Equal(new WindowPlacement(100, 80, 1400, 900, false),
            new WindowPlacement(100, 80, 1400, 900, false).FitTo(Screen));

    [Fact]
    public void OffScreenPlacement_IsDiscarded()
        => Assert.Null(new WindowPlacement(3000, 80, 1400, 900, false).FitTo(Screen));

    [Fact]
    public void TooBigPlacement_IsShrunkToScreen()
    {
        var fitted = new WindowPlacement(-50, 0, 4000, 2000, false).FitTo(Screen)!;
        Assert.True(fitted.Width <= Screen.Width && fitted.Height <= Screen.Height);
        Assert.True(fitted.Left >= 0);
    }

    [Fact]
    public void TinyPlacement_IsDiscarded()
        => Assert.Null(new WindowPlacement(10, 10, 100, 80, false).FitTo(Screen));
}
