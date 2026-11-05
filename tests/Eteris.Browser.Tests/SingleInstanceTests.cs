using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void TryAcquire_SecondCallWithSameName_ReturnsNull()
    {
        var name = "Eteris.Test." + Guid.NewGuid();
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));
    }

    [Fact]
    public void TryAcquire_AfterDispose_SucceedsAgain()
    {
        var name = "Eteris.Test." + Guid.NewGuid();
        SingleInstance.TryAcquire(name)!.Dispose();
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }
}
