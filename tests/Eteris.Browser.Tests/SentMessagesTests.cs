using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

// Espera pela criação do browser do CEF recebendo só as mensagens enviadas de outras threads (Browser/PageBrowser.cs).
public class SentMessagesTests
{
    [Fact]
    public void DeliverUntil_ReturnsWhenTheOtherThreadSignals()
    {
        using var done = new ManualResetEvent(false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = Task.Run(async () => { await Task.Delay(30); done.Set(); });
        Assert.True(SentMessages.DeliverUntil(done, TimeSpan.FromSeconds(5)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void DeliverUntil_GivesUpAtTheLimit()
    {
        using var never = new ManualResetEvent(false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SentMessages.DeliverUntil(never, TimeSpan.FromMilliseconds(60)));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void DeliverUntil_AlreadySignaledReturnsAtOnce()
    {
        using var done = new ManualResetEvent(true);
        Assert.True(SentMessages.DeliverUntil(done, TimeSpan.Zero));
        Assert.True(SentMessages.DeliverUntil(done, TimeSpan.FromSeconds(1)));
    }
}
